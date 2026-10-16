using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    public interface ITalearrAbsSyncService
    {
        TalearrAbsSyncState Sync(TalearrAbsConnection connection);
    }

    // TaleArr: reads each Audiobookshelf connection's libraries (GET only), maps the item paths to
    // TaleArr's root folders, matches items to books and replaces the connection's rows in
    // talearr.db. A failed read keeps the previous rows, so a short Audiobookshelf outage never
    // makes TaleArr forget the library and grab everything again.
    public class TalearrAbsSyncService : ITalearrAbsSyncService,
                                         IExecute<SyncAudiobookshelfLibraryCommand>,
                                         IHandle<ProviderAddedEvent<INotification>>,
                                         IHandle<ProviderUpdatedEvent<INotification>>,
                                         IHandle<ProviderDeletedEvent<INotification>>
    {
        private readonly ITalearrAbsConnections _connections;
        private readonly IAudioBookShelfProxy _proxy;
        private readonly ITalearrAbsClient _client;
        private readonly ITalearrAbsStore _store;
        private readonly ITalearrAbsLibraryService _libraryService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public TalearrAbsSyncService(ITalearrAbsConnections connections,
                                     IAudioBookShelfProxy proxy,
                                     ITalearrAbsClient client,
                                     ITalearrAbsStore store,
                                     ITalearrAbsLibraryService libraryService,
                                     IRootFolderService rootFolderService,
                                     IAuthorService authorService,
                                     IBookService bookService,
                                     IEditionService editionService,
                                     IMediaFileService mediaFileService,
                                     IManageCommandQueue commandQueueManager,
                                     Logger logger)
        {
            _connections = connections;
            _proxy = proxy;
            _client = client;
            _store = store;
            _libraryService = libraryService;
            _rootFolderService = rootFolderService;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Execute(SyncAudiobookshelfLibraryCommand message)
        {
            var connections = _connections.GetConnections();
            var syncing = connections.Where(c => c.SyncEnabled).ToList();

            // Forget connections that were deleted or had Sync Library turned off.
            _store.RemoveConnectionsExcept(syncing.Select(c => c.Id));

            if (message.ConnectionId.HasValue)
            {
                syncing = syncing.Where(c => c.Id == message.ConnectionId.Value).ToList();
            }

            var failures = new List<string>();
            foreach (var connection in syncing)
            {
                var state = Sync(connection);
                if (state.LastError.IsNotNullOrWhiteSpace())
                {
                    failures.Add($"{connection.Name}: {state.LastError}");
                }
            }

            _libraryService.Invalidate();

            if (failures.Count > 0)
            {
                throw new TalearrAbsSyncException("Audiobookshelf library sync failed for " + string.Join("; ", failures));
            }
        }

        public TalearrAbsSyncState Sync(TalearrAbsConnection connection)
        {
            var previous = _store.GetStates().FirstOrDefault(s => s.ConnectionId == connection.Id);
            var started = DateTime.UtcNow;

            try
            {
                var rootFolders = _rootFolderService.All() ?? new List<RootFolder>();
                var libraries = (_proxy.GetLibraries(connection.Settings) ?? new List<AudioBookShelfLibrary>())
                    .Where(l => l != null && l.Id.IsNotNullOrWhiteSpace() && string.Equals(l.MediaType, "book", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var selected = SelectLibraries(connection.Settings, libraries);
                var mappings = BuildFolderMappings(connection.Settings, libraries, rootFolders);
                var rootPaths = rootFolders.Select(r => r.Path).Where(p => p.IsNotNullOrWhiteSpace()).ToList();

                var result = new TalearrAbsSyncResult { ConnectionId = connection.Id };
                foreach (var library in selected)
                {
                    foreach (var raw in _client.GetLibraryItems(connection.Settings, library.Id))
                    {
                        var item = TalearrAbsItemReader.Read(raw, connection.Id, mappings, rootPaths, started);
                        if (item != null)
                        {
                            result.Items.Add(item);
                        }
                    }
                }

                var lookup = new TalearrAbsBookLookup(_authorService, _bookService, _editionService, _mediaFileService, rootPaths);
                foreach (var item in result.Items)
                {
                    try
                    {
                        result.Matches.AddRange(TalearrAbsMatcher.Match(item, lookup));
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "TaleArr: could not match Audiobookshelf item {0} ({1})", item.ItemId, item.Title);
                    }
                }

                var state = new TalearrAbsSyncState
                {
                    ConnectionId = connection.Id,
                    LastSyncAt = started,
                    ItemCount = result.Items.Count,
                    MatchedCount = result.Matches.Select(m => m.ItemId).Distinct().Count()
                };

                _store.ReplaceConnection(result, state);
                _logger.Info("TaleArr: read {0} items from Audiobookshelf ({1}), {2} matched to books", state.ItemCount, connection.Name, state.MatchedCount);
                return state;
            }
            catch (Exception ex)
            {
                var message = Describe(ex);
                _logger.Warn(ex, "TaleArr: could not read the Audiobookshelf library ({0}); keeping the last copy", connection.Name);

                var state = new TalearrAbsSyncState
                {
                    ConnectionId = connection.Id,
                    LastSyncAt = previous?.LastSyncAt,
                    LastError = message,
                    ItemCount = previous?.ItemCount ?? 0,
                    MatchedCount = previous?.MatchedCount ?? 0
                };

                _store.SaveState(state);
                return state;
            }
        }

        // Mapped libraries when the connection has library mappings, else the legacy library
        // picks, else every book library.
        public static List<AudioBookShelfLibrary> SelectLibraries(AudioBookShelfSettings settings, List<AudioBookShelfLibrary> libraries)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (settings.HasConfiguredLibraryMappings())
            {
                ids.UnionWith(settings.GetLibraryMappings().Where(m => m?.LibraryId.IsNotNullOrWhiteSpace() == true).Select(m => m.LibraryId));
            }
            else
            {
                foreach (var legacy in new[] { settings.AudiobookLibraryId, settings.EbookLibraryId, settings.LibraryId })
                {
                    if (legacy.IsNotNullOrWhiteSpace())
                    {
                        ids.Add(legacy);
                    }
                }
            }

            return ids.Count == 0 ? libraries : libraries.Where(l => ids.Contains(l.Id)).ToList();
        }

        public static List<TalearrAbsFolderMapping> BuildFolderMappings(AudioBookShelfSettings settings, List<AudioBookShelfLibrary> libraries, List<RootFolder> rootFolders)
        {
            var result = new List<TalearrAbsFolderMapping>();

            foreach (var mapping in settings.GetLibraryMappings().Where(m => m != null && m.LibraryId.IsNotNullOrWhiteSpace()))
            {
                var root = rootFolders.FirstOrDefault(r => r.Id == mapping.RootFolderId);
                if (root == null || root.Path.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var library = libraries.FirstOrDefault(l => string.Equals(l.Id, mapping.LibraryId, StringComparison.OrdinalIgnoreCase));
                var folders = library?.Folders ?? new List<AudioBookShelfLibraryFolder>();

                var folderPath = folders.FirstOrDefault(f => mapping.LibraryFolderId.IsNotNullOrWhiteSpace() &&
                                                             string.Equals(f.Id, mapping.LibraryFolderId, StringComparison.OrdinalIgnoreCase))?.FullPath ??
                                 mapping.LibraryFolderPath ??
                                 (folders.Count == 1 ? folders[0].FullPath : null);

                if (folderPath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                result.Add(new TalearrAbsFolderMapping
                {
                    LibraryId = mapping.LibraryId,
                    LibraryFolderId = mapping.LibraryFolderId,
                    AbsFolderPath = folderPath,
                    LocalRootPath = root.Path
                });
            }

            return result;
        }

        public void Handle(ProviderAddedEvent<INotification> message)
        {
            QueueFor(message.Definition);
        }

        public void Handle(ProviderUpdatedEvent<INotification> message)
        {
            QueueFor(message.Definition);
        }

        public void Handle(ProviderDeletedEvent<INotification> message)
        {
            _store.RemoveConnectionsExcept(_connections.GetConnections().Where(c => c.SyncEnabled && c.Id != message.ProviderId).Select(c => c.Id));
            _libraryService.Invalidate();
        }

        private void QueueFor(ThingiProvider.ProviderDefinition definition)
        {
            if (definition is not NotificationDefinition notification ||
                !string.Equals(notification.Implementation, TalearrAbsConnections.Implementation, StringComparison.OrdinalIgnoreCase) ||
                notification.Settings is not AudioBookShelfSettings settings)
            {
                return;
            }

            _libraryService.Invalidate();

            if (!settings.TalearrSyncLibrary)
            {
                _store.RemoveConnectionsExcept(_connections.GetConnections().Where(c => c.SyncEnabled && c.Id != notification.Id).Select(c => c.Id));
                return;
            }

            _commandQueueManager.Push(new SyncAudiobookshelfLibraryCommand { ConnectionId = notification.Id });
        }

        private static string Describe(Exception ex)
        {
            if (ex is HttpException http)
            {
                var status = (int)http.Response.StatusCode;
                return status == 401 || status == 403
                    ? $"Audiobookshelf refused the API key ({status})"
                    : $"Audiobookshelf answered {status}";
            }

            return ex.Message;
        }
    }

    public class TalearrAbsSyncException : Exception
    {
        public TalearrAbsSyncException(string message)
            : base(message)
        {
        }
    }
}
