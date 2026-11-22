import moment from 'moment';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import Command, { CommandBody } from 'Commands/Command';
import Icon from 'Components/Icon';
import IconButton from 'Components/Link/IconButton';
import Link from 'Components/Link/Link';
import { icons } from 'Helpers/Props';
import { cancelCommand, fetchCommands } from 'Store/Actions/commandActions';
import styles from './PendingAuthorImports.css';

// Talearr: author adds and refreshes that are queued or running (Chaptarr #359, #119), so a
// big import can be checked on and a queued add taken back. Cancelling uses the existing
// command cancel (DELETE /api/v1/command/{id}); only queued commands offer it, since a refresh
// that already started does not stop part way.

const AUTHOR_COMMANDS = [
  'RefreshAuthor',
  'BulkRefreshAuthor',
  'ImportDiscoveredAuthor',
];

const ACTIVE_STATUSES = ['queued', 'started'];

// Some of these commands (discovered-author imports) do not stream updates, so the list is
// re-read now and then while anything is pending.
const REFRESH_INTERVAL_MS = 15000;

interface AuthorCommandBody extends CommandBody {
  isNewAuthor?: boolean;
  areNewAuthors?: boolean;
  providerId?: string;
  discoveredAuthorFolderPath?: string;
}

function isPendingAuthorCommand(command: Command) {
  return (
    AUTHOR_COMMANDS.includes(command.name) &&
    ACTIVE_STATUSES.includes(command.status)
  );
}

function describe(command: Command, authorNames: Map<number, string>) {
  const body = command.body as AuthorCommandBody;
  const nameOf = (id: number) => authorNames.get(id) || `Author ${id}`;

  switch (command.name) {
    case 'RefreshAuthor':
      if (!body.authorId) {
        return 'Refresh all authors';
      }

      return `${body.isNewAuthor ? 'Add' : 'Refresh'} ${nameOf(body.authorId)}`;

    case 'BulkRefreshAuthor': {
      const ids = body.authorIds || [];
      const shown = ids.slice(0, 3).map(nameOf).join(', ');
      const more = ids.length > 3 ? ` and ${ids.length - 3} more` : '';

      return `${body.areNewAuthors ? 'Add' : 'Refresh'} ${shown}${more}`;
    }

    case 'ImportDiscoveredAuthor':
      return `Import ${
        body.discoveredAuthorFolderPath ||
        body.providerId ||
        'discovered author'
      }`;

    default:
      return command.commandName;
  }
}

export default function PendingAuthorImports() {
  const dispatch = useDispatch();
  const commands = useSelector((state: AppState) => state.commands.items);
  const authors = useSelector((state: AppState) => state.authors.items);
  const [isExpanded, setIsExpanded] = useState(false);

  const pending = useMemo(
    () => commands.filter(isPendingAuthorCommand),
    [commands]
  );

  const authorNames = useMemo(() => {
    return new Map(authors.map((author) => [author.id, author.authorName]));
  }, [authors]);

  const hasPending = pending.length > 0;

  useEffect(() => {
    if (!hasPending) {
      return undefined;
    }

    const intervalId = setInterval(() => {
      dispatch(fetchCommands());
    }, REFRESH_INTERVAL_MS);

    return () => clearInterval(intervalId);
  }, [hasPending, dispatch]);

  const onTogglePress = useCallback(() => {
    setIsExpanded((value) => !value);
  }, []);

  const onCancelPress = useCallback(
    (id: number) => {
      dispatch(cancelCommand({ id }));
    },
    [dispatch]
  );

  if (!hasPending) {
    return null;
  }

  const queuedCount = pending.filter((c) => c.status === 'queued').length;
  const runningCount = pending.length - queuedCount;

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <span>
          {`Pending author adds and refreshes: ${runningCount} running, ${queuedCount} queued`}
        </span>

        <Link onPress={onTogglePress}>{isExpanded ? 'Hide' : 'Show'}</Link>
      </div>

      {isExpanded ? (
        <ul className={styles.list}>
          {pending.map((command) => {
            const isQueued = command.status === 'queued';

            return (
              <li key={command.id} className={styles.item}>
                <Icon
                  name={isQueued ? icons.PENDING : icons.REFRESH}
                  isSpinning={!isQueued}
                />

                <span className={styles.label}>
                  {describe(command, authorNames)}
                </span>

                <span className={styles.status}>
                  {isQueued
                    ? `Queued ${moment(command.queued).fromNow()}`
                    : `Running since ${moment(command.started).fromNow()}`}
                </span>

                {isQueued ? (
                  <IconButton
                    title="Cancel"
                    name={icons.REMOVE}
                    onPress={() => onCancelPress(command.id)}
                  />
                ) : null}
              </li>
            );
          })}
        </ul>
      ) : null}
    </div>
  );
}
