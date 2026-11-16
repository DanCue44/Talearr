using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Api.V1.RootFolders;
using Chaptarr.Core.Test;
using Chaptarr.Http.REST;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.ReadOnlyLibrary;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class RootFolderControllerTypeChangeFixture
    {
        private const string TypeChangeAssignedMessage = "You can't split a mixed-content root folder or switch an assigned single-type root folder. Remove it and re-add it as audiobook-only or eBook-only.";

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            public RootFolder Existing { get; set; }
            public RootFolder Updated { get; private set; }
            public int UpdateCallCount { get; private set; }

            public List<RootFolder> All() => new();
            public List<RootFolder> AllWithSpaceStats() => new();
            public List<int> Removed { get; } = new();

            public RootFolder Add(RootFolder rootFolder)
            {
                rootFolder.Id = 42;
                return rootFolder;
            }

            public RootFolder Update(RootFolder rootFolder)
            {
                Updated = rootFolder;
                UpdateCallCount++;
                return rootFolder;
            }

            public void Remove(int id) => Removed.Add(id);
            public RootFolder Get(int id) => Existing?.Id == id ? Existing : null;
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly List<Author> _authors;

            public StubAuthorService(List<Author> authors)
            {
                _authors = authors;
            }

            public List<Author> GetAllAuthors(bool bypassCache = false) => _authors;

            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems, int? ebookQualityProfileId, int? ebookMetadataProfileId, bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();

            public List<(string RootFolderPath, BookMediaType MediaType, NewItemMonitorTypes MonitorNewItems)> MonitorNewItemsApplied { get; } = new();

            public List<Author> SetMonitorNewItemsForRootFolder(string rootFolderPath, BookMediaType mediaType, NewItemMonitorTypes monitorNewItems)
            {
                MonitorNewItemsApplied.Add((rootFolderPath, mediaType, monitorNewItems));
                return new List<Author>();
            }
        }

        private sealed class StubLocalizationService : ILocalizationService
        {
            public string LastPhrase { get; private set; }

            public Dictionary<string, string> GetLocalizationDictionary()
            {
                return new Dictionary<string, string>();
            }

            public string GetLocalizedString(string phrase)
            {
                LastPhrase = phrase;
                return phrase == "RootFolderTypeChangeAssignedMessage" ? TypeChangeAssignedMessage : phrase;
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return GetLocalizedString(phrase);
            }
        }

        private sealed class TestableRootFolderController : RootFolderController
        {
            public TestableRootFolderController(
                StubRootFolderService rootFolderService,
                StubAuthorService authorService,
                StubLocalizationService localizationService,
                ITalearrReadOnlyLibrary readOnlyLibrary = null)
                : base(
                    rootFolderService,
                    DispatchProxy.Create<IRootFolderScanService, ThrowingProxy<IRootFolderScanService>>(),
                    authorService,
                    DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                    ConfigServiceTestProxy.Create(),
                    localizationService,
                    DispatchProxy.Create<IBroadcastSignalRMessage, ThrowingProxy<IBroadcastSignalRMessage>>(),
                    new RecycleBinValidator(DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>()),
                    new RootFolderValidator(rootFolderService),
                    new PathExistsValidator(DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                    new MappedNetworkDriveValidator(
                        DispatchProxy.Create<IRuntimeInfo, ThrowingProxy<IRuntimeInfo>>(),
                        DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                    new StartupFolderValidator(DispatchProxy.Create<IAppFolderInfo, ThrowingProxy<IAppFolderInfo>>()),
                    new SystemFolderValidator(),
                    new FolderWritableValidator(DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                    new FolderReadableValidator(DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                    readOnlyLibrary)
            {
            }

            public ValidationResult ValidatePost(RootFolderResource resource)
            {
                return PostValidator.Validate(resource);
            }

            public ValidationResult ValidateShared(RootFolderResource resource)
            {
                return SharedValidator.Validate(resource);
            }
        }

        private static TestableRootFolderController BuildController(
            StubRootFolderService rootFolderService,
            StubAuthorService authorService,
            StubLocalizationService localizationService = null,
            ITalearrReadOnlyLibrary readOnlyLibrary = null)
        {
            localizationService ??= new StubLocalizationService();

            return new TestableRootFolderController(rootFolderService, authorService, localizationService, readOnlyLibrary);
        }

        // A read-only store that can lose the flag or fail on the save after the folder is added.
        private sealed class StubReadOnlyLibrary : ITalearrReadOnlyLibrary
        {
            private readonly HashSet<string> _roots = new();

            public bool KeepFlag { get; set; } = true;
            public bool FailAfterAdd { get; set; }
            public int SetCount { get; private set; }

            public IReadOnlyList<string> ReadOnlyRoots => _roots.ToList();

            public bool IsReadOnly(string path) => _roots.Contains(path);

            public void SetReadOnly(string rootFolderPath, bool readOnly)
            {
                SetCount++;
                if (FailAfterAdd && readOnly && SetCount > 1)
                {
                    throw new InvalidOperationException("save failed");
                }

                if (readOnly && KeepFlag)
                {
                    _roots.Add(rootFolderPath);
                }
                else
                {
                    _roots.Remove(rootFolderPath);
                }
            }

            public string RedirectForWrite(string path, BookMediaType mediaType) => path;
        }

        private static RootFolderResource ReadOnlyAudiobookRoot()
        {
            return new RootFolderResource
            {
                Name = "Media Library",
                Path = "/audiobooks/audiobookshelf/",
                FolderType = (int)FolderType.Audiobook,
                ReadOnly = true
            };
        }

        [Test]
        public void post_should_keep_a_read_only_root_when_the_flag_is_saved()
        {
            var rootFolders = new StubRootFolderService();
            var readOnly = new StubReadOnlyLibrary();
            var controller = BuildController(rootFolders, new StubAuthorService(new List<Author>()), readOnlyLibrary: readOnly);

            controller.CreateRootFolder(ReadOnlyAudiobookRoot());

            Assert.That(rootFolders.Removed, Is.Empty);
            Assert.That(readOnly.IsReadOnly("/audiobooks/audiobookshelf/"), Is.True);
        }

        [Test]
        public void post_should_remove_a_read_only_root_when_the_flag_does_not_stick()
        {
            var rootFolders = new StubRootFolderService();
            var readOnly = new StubReadOnlyLibrary { KeepFlag = false };
            var controller = BuildController(rootFolders, new StubAuthorService(new List<Author>()), readOnlyLibrary: readOnly);

            var ex = Assert.Throws<BadRequestException>(() => controller.CreateRootFolder(ReadOnlyAudiobookRoot()));

            Assert.That(ex.Message, Does.Contain("read-only"));
            Assert.That(rootFolders.Removed, Is.EqualTo(new[] { 42 }));
        }

        [Test]
        public void post_should_remove_a_read_only_root_when_saving_the_flag_fails()
        {
            var rootFolders = new StubRootFolderService();
            var readOnly = new StubReadOnlyLibrary { FailAfterAdd = true };
            var controller = BuildController(rootFolders, new StubAuthorService(new List<Author>()), readOnlyLibrary: readOnly);

            Assert.Throws<InvalidOperationException>(() => controller.CreateRootFolder(ReadOnlyAudiobookRoot()));

            Assert.That(rootFolders.Removed, Is.EqualTo(new[] { 42 }));
            Assert.That(readOnly.IsReadOnly("/audiobooks/audiobookshelf/"), Is.False);
        }

        [Test]
        public void put_should_report_a_read_only_flag_that_does_not_stick()
        {
            var rootFolders = new StubRootFolderService
            {
                Existing = new RootFolder { Id = 7, Path = "/audiobooks/audiobookshelf/", FolderType = FolderType.Audiobook }
            };
            var readOnly = new StubReadOnlyLibrary { KeepFlag = false };
            var controller = BuildController(rootFolders, new StubAuthorService(new List<Author>()), readOnlyLibrary: readOnly);
            var resource = ReadOnlyAudiobookRoot();
            resource.Id = 7;

            Assert.Throws<BadRequestException>(() => controller.UpdateRootFolder(resource));
        }

        [Test]
        public void post_should_require_an_explicit_folder_type_in_json()
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RootFolderResource>(
                "{\"name\":\"Books\",\"path\":\"/books\"}",
                STJson.GetSerializerSettings()));
        }

        [Test]
        public void should_reject_an_out_of_range_folder_type()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidateShared(new RootFolderResource
            {
                Name = "Books",
                Path = null,
                FolderType = 99
            });

            Assert.That(result.Errors.Any(failure => failure.ErrorMessage.Contains("Invalid folder type")), Is.True);
        }

        [TestCase(FolderType.Audiobook)]
        [TestCase(FolderType.Ebook)]
        public void post_should_reject_a_single_type_root_without_profile_defaults(FolderType folderType)
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidatePost(new RootFolderResource
            {
                Name = "Books",
                Path = "/books".AsOsAgnostic(),
                FolderType = (int)folderType
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(failure =>
                failure.ErrorMessage.Contains("quality profile") || failure.ErrorMessage.Contains("metadata profile")), Is.True);
        }

        [Test]
        public void post_should_reject_a_mixed_root_without_any_media_defaults()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidatePost(new RootFolderResource
            {
                Name = "Books",
                Path = "/books".AsOsAgnostic(),
                FolderType = (int)FolderType.Mixed
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(failure => failure.ErrorMessage.Contains("at least one media type")), Is.True);
        }

        [Test]
        public void post_should_allow_a_mixed_root_with_one_complete_media_side_and_no_monitoring_defaults()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidatePost(new RootFolderResource
            {
                Name = "Books",
                Path = "/books".AsOsAgnostic(),
                FolderType = (int)FolderType.Mixed,
                AudiobookQualityProfileId = 10,
                AudiobookMetadataProfileId = 20
            });

            Assert.That(result.IsValid, Is.True, () => string.Join(Environment.NewLine, result.Errors));
        }

        [Test]
        public void post_should_reject_an_incomplete_second_side_on_a_mixed_root()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidatePost(new RootFolderResource
            {
                Name = "Books",
                Path = "/books".AsOsAgnostic(),
                FolderType = (int)FolderType.Mixed,
                AudiobookQualityProfileId = 10,
                AudiobookMetadataProfileId = 20,
                EbookMonitored = false
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(failure => failure.PropertyName.StartsWith("ebook", StringComparison.OrdinalIgnoreCase)), Is.True);
        }

        [Test]
        public void should_reject_direct_single_type_change_when_authors_are_assigned_to_root_folder()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Ebook
                }
            };

            var localizationService = new StubLocalizationService();
            var controller = BuildController(
                rootFolderService,
                new StubAuthorService(new List<Author>
                {
                    new()
                    {
                        Id = 100,
                        Name = "Greg Pak",
                        EbookRootFolderPath = "/books"
                    }
                }),
                localizationService);

            var resource = new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Audiobook
            };

            var ex = Assert.Throws<BadRequestException>(() => controller.UpdateRootFolder(resource));

            Assert.That(ex.Message, Does.EndWith(TypeChangeAssignedMessage));
            Assert.That(localizationService.LastPhrase, Is.EqualTo("RootFolderTypeChangeAssignedMessage"));
            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(0));
        }

        [TestCase(FolderType.Audiobook)]
        [TestCase(FolderType.Ebook)]
        public void should_allow_widening_to_mixed_when_authors_are_assigned(FolderType existingType)
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = existingType
                }
            };

            var controller = BuildController(
                rootFolderService,
                new StubAuthorService(new List<Author>
                {
                    new()
                    {
                        Id = 100,
                        Name = "Assigned Author",
                        AudiobookRootFolderPath = "/books",
                        EbookRootFolderPath = "/books"
                    }
                }));

            controller.UpdateRootFolder(new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Mixed
            });

            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(1));
            Assert.That(rootFolderService.Updated.FolderType, Is.EqualTo(FolderType.Mixed));
        }

        [Test]
        public void should_reject_narrowing_mixed_root_when_authors_are_assigned()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Mixed
                }
            };
            var controller = BuildController(
                rootFolderService,
                new StubAuthorService(new List<Author>
                {
                    new()
                    {
                        Id = 100,
                        Name = "Assigned Author",
                        AudiobookRootFolderPath = "/books"
                    }
                }));

            var ex = Assert.Throws<BadRequestException>(() => controller.UpdateRootFolder(new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Audiobook
            }));

            Assert.That(ex.Message, Does.EndWith(TypeChangeAssignedMessage));
            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(0));
        }

        [Test]
        public void should_allow_type_change_when_root_folder_is_unassigned()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Ebook
                }
            };

            var controller = BuildController(rootFolderService, new StubAuthorService(new List<Author>()));

            var resource = new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Audiobook
            };

            controller.UpdateRootFolder(resource);

            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(1));
            Assert.That(rootFolderService.Updated.FolderType, Is.EqualTo(FolderType.Audiobook));
        }

        [Test]
        public void should_still_reject_root_folder_path_edits()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Ebook
                }
            };
            var controller = BuildController(rootFolderService, new StubAuthorService(new List<Author>()));

            var ex = Assert.Throws<BadRequestException>(() => controller.UpdateRootFolder(new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/other-books",
                FolderType = (int)FolderType.Ebook
            }));

            Assert.That(ex.Message, Does.EndWith("Cannot edit root folder path"));
            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(0));
        }

        [Test]
        public void resource_should_preserve_default_sync_monitored_across_formats()
        {
            var resource = new RootFolderResource
            {
                Name = "Mixed",
                Path = "/library",
                FolderType = (int)FolderType.Mixed,
                DefaultSyncMonitoredAcrossFormats = true,
                AudiobookMonitored = true,
                AudiobookMonitorExistingMode = MonitorTypes.Missing,
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitored = false,
                EbookMonitorExistingMode = MonitorTypes.Existing,
                EbookMonitorNewItems = NewItemMonitorTypes.None
            };

            var model = resource.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(model.DefaultSyncMonitoredAcrossFormats, Is.True);
                Assert.That(model.GetAudiobookSettings().Monitored, Is.True);
                Assert.That(model.GetAudiobookSettings().MonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                Assert.That(model.GetAudiobookSettings().MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(model.GetEbookSettings().Monitored, Is.False);
                Assert.That(model.GetEbookSettings().MonitorExistingMode, Is.EqualTo(MonitorTypes.Existing));
                Assert.That(model.GetEbookSettings().MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [Test]
        public void structured_canonical_initial_mode_should_win_over_the_deprecated_boolean()
        {
            var resource = new RootFolderResource
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = (int)FolderType.Audiobook,
                Audiobook = new MediaTypeSettingsResource
                {
                    Monitored = true,
                    MonitorExistingMode = MonitorTypes.Missing,
                    MonitorExistingBooks = false,
                    MonitorNewItems = NewItemMonitorTypes.None
                }
            };

            var settings = resource.ToModel().GetAudiobookSettings();

            Assert.That(settings.MonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
        }



        [Test]
        public void resource_should_round_trip_audiobookshelf_sidecar_settings_per_media_type()
        {
            var resource = new RootFolderResource
            {
                Name = "Mixed",
                Path = "/library",
                FolderType = (int)FolderType.Mixed,
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.New,
                AudiobookWriteAudioBookShelfMetadataJson = true,
                AudiobookWriteAudioBookShelfCover = false,
                EbookWriteAudioBookShelfMetadataJson = false,
                EbookWriteAudioBookShelfCover = true
            };

            var model = resource.ToModel();
            var roundTripped = model.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(model.GetAudiobookSettings().WriteAudioBookShelfMetadataJson, Is.True);
                Assert.That(model.GetAudiobookSettings().WriteAudioBookShelfCover, Is.False);
                Assert.That(model.GetEbookSettings().WriteAudioBookShelfMetadataJson, Is.False);
                Assert.That(model.GetEbookSettings().WriteAudioBookShelfCover, Is.True);
                Assert.That(roundTripped.AudiobookWriteAudioBookShelfMetadataJson, Is.True);
                Assert.That(roundTripped.AudiobookWriteAudioBookShelfCover, Is.False);
                Assert.That(roundTripped.EbookWriteAudioBookShelfMetadataJson, Is.False);
                Assert.That(roundTripped.EbookWriteAudioBookShelfCover, Is.True);
            });
        }

        [Test]
        public void resource_should_allow_empty_opposite_media_settings_for_single_type_folder()
        {
            var resource = new RootFolderResource
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = (int)FolderType.Audiobook,
                AudiobookMonitored = true,
                Ebook = new MediaTypeSettingsResource(),
                EbookTags = new List<int>()
            };

            var model = resource.ToModel();

            Assert.That(model.FolderType, Is.EqualTo(FolderType.Audiobook));
            Assert.That(model.GetEbookSettings(), Is.Null);
        }

        [Test]
        public void resource_should_translate_legacy_selected_future_to_binary_settings()
        {
            var resource = new RootFolderResource
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = (int)FolderType.Audiobook,
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = true
            };

            var settings = resource.ToModel().GetAudiobookSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Monitored, Is.True);
                Assert.That(settings.MonitorExistingBooks, Is.False);
                Assert.That(settings.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            });
        }

        [Test]
        public void resource_should_project_binary_settings_for_legacy_root_clients()
        {
            var root = new RootFolder
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = FolderType.Audiobook
            };
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                Monitored = true,
                MonitorExistingBooks = false,
                MonitorNewItems = NewItemMonitorTypes.New
            });

            var resource = root.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.AudiobookMonitorExisting, Is.EqualTo(2));
                Assert.That(resource.AudiobookMonitorFuture, Is.True);
                Assert.That(resource.Audiobook.MonitorExisting, Is.EqualTo(2));
                Assert.That(resource.Audiobook.MonitorFuture, Is.True);
            });
        }

        [Test]
        public void legacy_root_monitoring_json_should_migrate_selected_future_to_binary_settings()
        {
            var root = new RootFolder
            {
                AudiobookSettings = "{\"MonitorExisting\":2,\"MonitorFuture\":true}"
            };

            var settings = root.GetAudiobookSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Monitored, Is.True);
                Assert.That(settings.MonitorExistingBooks, Is.False);
                Assert.That(settings.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            });
        }

        [Test]
        public void canonical_root_json_should_round_trip_the_full_initial_mode_without_writing_the_old_boolean()
        {
            var root = new RootFolder();
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                Monitored = true,
                MonitorExistingMode = MonitorTypes.Missing,
                MonitorNewItems = NewItemMonitorTypes.All
            });

            var settings = root.GetAudiobookSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.MonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                Assert.That(root.AudiobookSettings, Does.Contain("\"MonitorExistingMode\""));
                Assert.That(root.AudiobookSettings, Does.Not.Contain("\"MonitorExistingBooks\""));
            });
        }

        private static (StubRootFolderService RootFolders, StubAuthorService Authors, TestableRootFolderController Controller) BuildMixedRootForApply()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Library",
                    Path = "/library",
                    FolderType = FolderType.Mixed
                }
            };
            var authorService = new StubAuthorService(new List<Author>
            {
                new Author { Id = 1, AudiobookRootFolderPath = "/library", EbookRootFolderPath = "/library" }
            });

            return (rootFolderService, authorService, BuildController(rootFolderService, authorService));
        }

        private static RootFolderResource MixedRootWithMonitorNewItems(NewItemMonitorTypes audiobook, NewItemMonitorTypes ebook)
        {
            return new RootFolderResource
            {
                Id = 42,
                Name = "Library",
                Path = "/library",
                FolderType = (int)FolderType.Mixed,
                AudiobookQualityProfileId = 1,
                AudiobookMetadataProfileId = 2,
                AudiobookMonitorNewItems = audiobook,
                EbookQualityProfileId = 3,
                EbookMetadataProfileId = 4,
                EbookMonitorNewItems = ebook
            };
        }

        // Issue #8: saving a root folder without the apply option never touches existing authors.
        [Test]
        public void put_without_apply_flags_should_not_change_existing_authors()
        {
            var (rootFolders, authors, controller) = BuildMixedRootForApply();

            controller.UpdateRootFolder(MixedRootWithMonitorNewItems(NewItemMonitorTypes.None, NewItemMonitorTypes.None));

            Assert.That(rootFolders.UpdateCallCount, Is.EqualTo(1));
            Assert.That(authors.MonitorNewItemsApplied, Is.Empty);
        }

        // Issue #8: the apply option copies only the chosen format's saved Monitor New Books value.
        [Test]
        public void put_with_ebook_apply_flag_should_apply_only_the_ebook_monitor_new_items_value()
        {
            var (_, authors, controller) = BuildMixedRootForApply();

            controller.UpdateRootFolder(MixedRootWithMonitorNewItems(NewItemMonitorTypes.All, NewItemMonitorTypes.None),
                applyAudiobookMonitorNewItemsToAuthors: false,
                applyEbookMonitorNewItemsToAuthors: true);

            Assert.That(authors.MonitorNewItemsApplied, Has.Count.EqualTo(1));
            Assert.That(authors.MonitorNewItemsApplied[0].RootFolderPath, Is.EqualTo("/library"));
            Assert.That(authors.MonitorNewItemsApplied[0].MediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(authors.MonitorNewItemsApplied[0].MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
        }

        [Test]
        public void put_with_apply_flag_for_a_format_the_folder_does_not_hold_should_do_nothing()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder { Id = 42, Name = "Audio", Path = "/audio", FolderType = FolderType.Audiobook }
            };
            var authorService = new StubAuthorService(new List<Author>());
            var controller = BuildController(rootFolderService, authorService);

            controller.UpdateRootFolder(new RootFolderResource
            {
                Id = 42,
                Name = "Audio",
                Path = "/audio",
                FolderType = (int)FolderType.Audiobook,
                AudiobookQualityProfileId = 1,
                AudiobookMetadataProfileId = 2,
                AudiobookMonitorNewItems = NewItemMonitorTypes.None
            },
            applyAudiobookMonitorNewItemsToAuthors: false,
            applyEbookMonitorNewItemsToAuthors: true);

            Assert.That(authorService.MonitorNewItemsApplied, Is.Empty);
        }

        [Test]
        public void get_should_report_author_counts_per_format()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder { Id = 42, Name = "Library", Path = "/library", FolderType = FolderType.Mixed }
            };
            var authorService = new StubAuthorService(new List<Author>
            {
                new Author { Id = 1, AudiobookRootFolderPath = "/library", EbookRootFolderPath = "/library" },
                new Author { Id = 2, AudiobookRootFolderPath = "/library/", EbookRootFolderPath = "/other" },
                new Author { Id = 3, AudiobookRootFolderPath = "/library-two" }
            });
            var controller = BuildController(rootFolderService, authorService);

            var resource = controller.GetResourceByIdWithErrorHandler(42).Value;

            Assert.That(resource.AudiobookAuthorCount, Is.EqualTo(2));
            Assert.That(resource.EbookAuthorCount, Is.EqualTo(1));
        }

        // Issue #8 follow-up: with the folder already on None, the dialog needs to know how many
        // authors in it still differ, so it can offer to apply None without changing the folder first.
        [Test]
        public void get_should_report_authors_that_do_not_match_the_folder_monitor_new_items()
        {
            var folder = new RootFolder { Id = 42, Name = "Library", Path = "/library", FolderType = FolderType.Mixed };
            folder.SetAudiobookSettings(new MediaTypeSettings { MonitorNewItems = NewItemMonitorTypes.None });
            var rootFolderService = new StubRootFolderService { Existing = folder };
            var authorService = new StubAuthorService(new List<Author>
            {
                new Author { Id = 1, AudiobookRootFolderPath = "/library", AudiobookMonitorNewItems = NewItemMonitorTypes.None },
                new Author { Id = 2, AudiobookRootFolderPath = "/library", AudiobookMonitorNewItems = NewItemMonitorTypes.All },
                new Author { Id = 3, AudiobookRootFolderPath = "/library/" },
                new Author { Id = 4, AudiobookRootFolderPath = "/other", AudiobookMonitorNewItems = NewItemMonitorTypes.All }
            });
            var controller = BuildController(rootFolderService, authorService);

            var resource = controller.GetResourceByIdWithErrorHandler(42).Value;

            Assert.That(resource.AudiobookAuthorCount, Is.EqualTo(3));
            Assert.That(resource.AudiobookAuthorMismatchCount, Is.EqualTo(2));
            Assert.That(resource.EbookAuthorMismatchCount, Is.EqualTo(0));
        }

        // Issue #8: a root folder saved with "Books to Monitor When Added" and "Monitor New Books"
        // set to None must read back as None, and must stay None when a client sends the resource
        // it read straight back (old Chaptarr-shaped fields included).
        [TestCase(FolderType.Audiobook, true)]
        [TestCase(FolderType.Audiobook, false)]
        [TestCase(FolderType.Mixed, true)]
        [TestCase(FolderType.Mixed, false)]
        public void none_monitoring_defaults_should_survive_save_read_and_resave(FolderType folderType, bool monitored)
        {
            var gate = monitored ? "true" : "false";
            var ebookSide = folderType == FolderType.Mixed
                ? "\"ebookMonitored\":" + gate + ",\"ebookMonitorExistingMode\":\"none\",\"ebookMonitorNewItems\":\"none\",\"ebookQualityProfileId\":3,\"ebookMetadataProfileId\":4,\"ebookTags\":[]"
                : "\"ebookMonitored\":null,\"ebookMonitorExistingMode\":null,\"ebookMonitorNewItems\":null,\"ebookQualityProfileId\":null,\"ebookMetadataProfileId\":null,\"ebookTags\":null";

            // The body the settings page sends when the folder is saved.
            var json = "{\"name\":\"Library\",\"path\":\"/audiobooks/library\",\"folderType\":" + (int)folderType +
                       ",\"defaultTags\":[],\"isCalibreLibrary\":false,\"readOnly\":false,\"placeEbooksWithAudiobooks\":false," +
                       "\"audiobookMonitored\":" + gate + ",\"audiobookMonitorExistingMode\":\"none\",\"audiobookMonitorNewItems\":\"none\"," +
                       "\"audiobookQualityProfileId\":1,\"audiobookMetadataProfileId\":2,\"audiobookTags\":[]," + ebookSide + "}";

            for (var save = 0; save < 3; save++)
            {
                var stored = STJson.Deserialize<RootFolderResource>(json).ToModel();
                var read = new RootFolder
                {
                    Name = stored.Name,
                    Path = stored.Path,
                    FolderType = stored.FolderType,
                    AudiobookSettings = stored.AudiobookSettings,
                    EbookSettings = stored.EbookSettings
                }.ToResource();

                Assert.Multiple(() =>
                {
                    Assert.That(read.AudiobookMonitored, Is.EqualTo(monitored));
                    Assert.That(read.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                    Assert.That(read.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));

                    if (folderType == FolderType.Mixed)
                    {
                        Assert.That(read.EbookMonitored, Is.EqualTo(monitored));
                        Assert.That(read.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                        Assert.That(read.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    }
                });

                // Next pass saves exactly what was read.
                json = STJson.ToJson(read);
            }
        }
    }
}
