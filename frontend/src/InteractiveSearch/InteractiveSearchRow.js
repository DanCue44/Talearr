import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import ProtocolLabel from 'Activity/Queue/ProtocolLabel';
import BookFormats from 'Book/BookFormats';
import BookQuality from 'Book/BookQuality';
import IndexerFlags from 'Book/IndexerFlags';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import tableRowStyles from 'Components/Table/TableRow.css';
import Popover from 'Components/Tooltip/Popover';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import formatDateTime from 'Utilities/Date/formatDateTime';
import formatAge from 'Utilities/Number/formatAge';
import formatBytes from 'Utilities/Number/formatBytes';
import formatCustomFormatScore from 'Utilities/Number/formatCustomFormatScore';
import translate from 'Utilities/String/translate';
import getRejectionDisplay from './getRejectionDisplay';
import Peers from './Peers';
import styles from './InteractiveSearchRow.css';

function getDownloadIcon(isGrabbing, isGrabbed, grabError) {
  if (isGrabbing) {
    return icons.SPINNER;
  } else if (isGrabbed) {
    return icons.DOWNLOADING;
  } else if (grabError) {
    return icons.DOWNLOADING;
  }

  return icons.DOWNLOAD;
}

function getDownloadKind(isGrabbed, grabError, downloadAllowed) {
  if (isGrabbed) {
    return kinds.SUCCESS;
  }

  if (grabError || !downloadAllowed) {
    return kinds.DANGER;
  }

  return kinds.DEFAULT;
}

function getDownloadTooltip(isGrabbing, isGrabbed, grabError) {
  if (isGrabbing) {
    return '';
  } else if (isGrabbed) {
    return translate('AddedToDownloadedQueue');
  } else if (grabError) {
    return grabError;
  }

  return translate('AddToDownloadedQueue');
}

function hasResolvedGrabTarget(authorId, bookId) {
  return authorId != null && bookId != null;
}

function renderTitleCharSpan(title, matchedTitleCharStart, matchedTitleCharEnd) {
  if (!Number.isInteger(matchedTitleCharStart) || !Number.isInteger(matchedTitleCharEnd)) {
    return null;
  }

  if (matchedTitleCharStart < 0 || matchedTitleCharEnd <= matchedTitleCharStart || matchedTitleCharEnd > title.length) {
    return null;
  }

  return (
    <>
      {title.slice(0, matchedTitleCharStart)}
      <span className={styles.titleMatch}>{title.slice(matchedTitleCharStart, matchedTitleCharEnd)}</span>
      {title.slice(matchedTitleCharEnd)}
    </>
  );
}

function renderTitleWithMatch(title, matchedTitleCharStart, matchedTitleCharEnd) {
  if (!title) {
    return title;
  }

  return renderTitleCharSpan(title, matchedTitleCharStart, matchedTitleCharEnd) || title;
}

function getNarratorDisplay(narrator) {
  const rawNarrator = typeof narrator === 'string' ? narrator.trim() : '';

  if (!rawNarrator) {
    return {
      displayName: '',
      hasMore: false,
      tooltip: null
    };
  }

  const hasPlusMore = (/\+\d+\s*$/).test(rawNarrator);
  const narratorListText = rawNarrator.replace(/\s*\+\d+\s*$/, '').trim();
  const narratorNames = narratorListText
    .split(/\s*,\s*/)
    .map((name) => name.trim())
    .filter((name) => name);

  const displayName = narratorNames[0] || rawNarrator;
  const hasMore = hasPlusMore || narratorNames.length > 1;
  const tooltipNames = !hasPlusMore && narratorNames.length > 1 ? narratorNames : [rawNarrator];

  return {
    displayName,
    hasMore,
    tooltip: tooltipNames
  };
}

function renderNarratorDisplay(narrator) {
  const narratorDisplay = getNarratorDisplay(narrator);

  if (!narratorDisplay.displayName) {
    return null;
  }

  if (!narratorDisplay.hasMore) {
    return (
      <span
        className={styles.narratorName}
        title={narrator || ''}
      >
        {narratorDisplay.displayName}
      </span>
    );
  }

  return (
    <Tooltip
      anchor={
        <span
          className={styles.narratorName}
          title={narrator || ''}
        >
          {narratorDisplay.displayName}
          <span className={styles.moreIndicator}>{', …'}</span>
        </span>
      }
      tooltip={
        <div>
          {narratorDisplay.tooltip.map((name) => (
            <div key={name}>{name}</div>
          ))}
        </div>
      }
      position={tooltipPositions.TOP}
    />
  );
}

// Talearr: bundles are kept visible and marked; automatic grabs use them only as a last resort.
function getBundleTooltip(isBundleFallback, bundleContents) {
  const text = isBundleFallback ?
    'Last resort: only the requested book is kept; other books are removed unless they are on your wanted list' :
    'Contains several books: only the requested book is kept; other books are removed unless they are on your wanted list';

  return bundleContents ? `${text} (${bundleContents})` : text;
}

// Talearr: a release grabbed for this book before. Grabbing it again only collides with the
// copy the download client already has, so the row says so and the grab asks first.
const grabbedInClientStates = ['downloading', 'importPending', 'importing', 'importBlocked'];

const grabbedStateLabels = {
  downloading: 'Already grabbed: downloading',
  importPending: 'Already grabbed: waiting to import',
  importing: 'Already grabbed: importing',
  importBlocked: 'Already grabbed: import blocked',
  imported: 'Already grabbed: imported',
  failed: 'Grabbed before: failed',
  grabbed: 'Grabbed before'
};

function isGrabbedInClient(grabbedState) {
  return grabbedInClientStates.includes(grabbedState);
}

function getGrabbedLabel(grabbedState) {
  return grabbedStateLabels[grabbedState] || grabbedStateLabels.grabbed;
}

function getGrabbedTooltip(grabbedAt, grabbedDownloadClient, longDateFormat, timeFormat) {
  const parts = [];

  if (grabbedAt) {
    parts.push(`Grabbed ${formatDateTime(grabbedAt, longDateFormat, timeFormat)}`);
  }

  if (grabbedDownloadClient) {
    parts.push(`in ${grabbedDownloadClient}`);
  }

  return parts.join(' ');
}

function getRegrabMessage(title, grabbedState, grabbedDownloadClient) {
  const client = grabbedDownloadClient || 'the download client';

  if (isGrabbedInClient(grabbedState)) {
    return `"${title}" is already in ${client} (${getGrabbedLabel(grabbedState).replace(/^Already grabbed: /, '')}). ` +
      'Grabbing it again does not download anything new, and the download client may refuse it. ' +
      'To finish it, retry or manually import it from Activity > Queue instead.';
  }

  if (grabbedState === 'imported') {
    return `"${title}" was grabbed and imported before. Grab it again anyway?`;
  }

  return `"${title}" was grabbed before (${getGrabbedLabel(grabbedState).replace(/^Grabbed before:? ?/, '') || 'no longer in the download client'}). Grab it again anyway?`;
}

class InteractiveSearchRow extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isConfirmGrabModalOpen: false,
      isConfirmRegrabModalOpen: false
    };
  }

  //
  // Listeners

  onGrabPress = () => {
    const {
      guid,
      indexerId,
      authorId,
      bookId,
      onGrabPress
    } = this.props;

    onGrabPress({
      guid,
      indexerId,
      authorId,
      bookId
    });
  };

  onRegrabPress = () => {
    this.setState({ isConfirmRegrabModalOpen: true });
  };

  onRegrabConfirm = () => {
    this.setState({ isConfirmRegrabModalOpen: false });

    const {
      authorId,
      bookId,
      downloadAllowed
    } = this.props;

    if (downloadAllowed || hasResolvedGrabTarget(authorId, bookId)) {
      this.onGrabPress();
    } else {
      this.onConfirmGrabPress();
    }
  };

  onRegrabCancel = () => {
    this.setState({ isConfirmRegrabModalOpen: false });
  };

  onConfirmGrabPress = () => {
    this.setState({ isConfirmGrabModalOpen: true });
  };

  onGrabConfirm = () => {
    this.setState({ isConfirmGrabModalOpen: false });

    const {
      guid,
      indexerId,
      searchPayload,
      onGrabPress
    } = this.props;

    onGrabPress({
      guid,
      indexerId,
      ...searchPayload
    });
  };

  onGrabCancel = () => {
    this.setState({ isConfirmGrabModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      rank,
      protocol,
      age,
      ageHours,
      ageMinutes,
      publishDate,
      title,
      displayTitle,
      matchedTitleCharStart,
      matchedTitleCharEnd,
      infoUrl,
      indexer,
      size,
      seeders,
      leechers,
      quality,
      customFormatScore,
      customFormats,
      indexerFlags = 0,
      isGraphicAudio,
      isBundle,
      isBundleFallback,
      bundleContents,
      authorId,
      bookId,
      rejections,
      rejectionDetails,
      downloadAllowed,
      isGrabbing,
      isGrabbed,
      isPreferredChoice,
      alreadyGrabbed,
      grabbedState,
      grabbedAt,
      grabbedDownloadClient,
      longDateFormat,
      timeFormat,
      grabError
    } = this.props;

    const titleText = renderTitleWithMatch(displayTitle || title, matchedTitleCharStart, matchedTitleCharEnd);
    const narratorDisplay = renderNarratorDisplay(this.props.narrator);
    const resolvedGrabTarget = hasResolvedGrabTarget(authorId, bookId);
    const rejectionItems = getRejectionDisplay(rejections, rejectionDetails);
    const rowClassName = classNames(
      tableRowStyles.row,
      isPreferredChoice && styles.preferredRow,
      alreadyGrabbed && styles.grabbedRow
    );
    const grabPress = downloadAllowed || resolvedGrabTarget ? this.onGrabPress : this.onConfirmGrabPress;

    return (
      <TableRow className={rowClassName}>
        <TableRowCell className={styles.rank}>
          {rank}
        </TableRowCell>

        <TableRowCell className={styles.protocol}>
          <ProtocolLabel
            protocol={protocol}
          />
        </TableRowCell>

        <TableRowCell
          className={styles.age}
          title={formatDateTime(publishDate, longDateFormat, timeFormat, { includeSeconds: true })}
        >
          {formatAge(age, ageHours, ageMinutes)}
        </TableRowCell>

        <TableRowCell className={styles.title}>
          <div className={styles.titleContent}>
            <div className={styles.titleTextBlock}>
              <Link
                to={infoUrl}
                title={title}
                className={styles.titleLink}
              >
                {titleText}
              </Link>
              {grabError ? (
                <div className={styles.grabError}>
                  {grabError}
                </div>
              ) : null}
            </div>
            {isPreferredChoice ? (
              <span className={styles.preferredBadge}>{translate('Preferred')}</span>
            ) : null}
            {alreadyGrabbed ? (
              <Label
                kind={isGrabbedInClient(grabbedState) ? kinds.WARNING : kinds.INFO}
                title={getGrabbedTooltip(grabbedAt, grabbedDownloadClient, longDateFormat, timeFormat)}
              >
                {getGrabbedLabel(grabbedState)}
              </Label>
            ) : null}
            {isBundle ? (
              <Label
                kind={kinds.WARNING}
                title={getBundleTooltip(isBundleFallback, bundleContents)}
              >
                {'Bundle'}
              </Label>
            ) : null}
          </div>
        </TableRowCell>

        <TableRowCell className={styles.indexer}>
          {indexer}
        </TableRowCell>

        <TableRowCell className={styles.size}>
          {formatBytes(size)}
        </TableRowCell>

        <TableRowCell className={styles.peers}>
          {
            protocol === 'torrent' &&
              <Peers
                seeders={seeders}
                leechers={leechers}
              />
          }
        </TableRowCell>

        <TableRowCell className={styles.duration}>
          {this.props.duration || ''}
        </TableRowCell>

        <TableRowCell className={styles.narrator}>
          <div className={styles.narratorContent}>
            {narratorDisplay}
            {isGraphicAudio ? (
              <Label
                kind={kinds.INFO}
                title={translate('InteractiveSearchDramatizedTitle')}
              >
                {translate('InteractiveSearchDramatized')}
              </Label>
            ) : null}
          </div>
        </TableRowCell>

        <TableRowCell className={styles.quality}>
          <BookQuality quality={quality} showRevision={true} />
        </TableRowCell>

        <TableRowCell className={styles.customFormatScore}>
          <Tooltip
            anchor={
              formatCustomFormatScore(customFormatScore, customFormats.length)
            }
            tooltip={<BookFormats formats={customFormats} />}
            position={tooltipPositions.LEFT}
          />
        </TableRowCell>

        <TableRowCell className={styles.indexerFlags}>
          {indexerFlags ? (
            <Popover
              anchor={<Icon name={icons.FLAG} kind={kinds.PRIMARY} />}
              title={translate('IndexerFlags')}
              body={<IndexerFlags indexerFlags={indexerFlags} />}
              position={tooltipPositions.LEFT}
            />
          ) : null}
        </TableRowCell>

        <TableRowCell className={styles.rejected}>
          {
            !!rejectionItems.length &&
              <Popover
                anchor={
                  <span className={styles.rejectionAnchor}>
                    <Icon
                      name={icons.DANGER}
                      kind={kinds.DANGER}
                    />
                    <span className={styles.rejectionLabel}>
                      {rejectionItems[0].label}
                    </span>
                    {
                      rejectionItems.length > 1 ?
                        <span className={styles.rejectionMore}>
                          +{rejectionItems.length - 1}
                        </span> :
                        null
                    }
                  </span>
                }
                title={translate('ReleaseRejected')}
                body={
                  <ul className={styles.rejectionList}>
                    {
                      rejectionItems.map((item, index) => {
                        return (
                          <li
                            key={index}
                            className={styles.rejectionItem}
                          >
                            <span className={styles.rejectionItemLabel}>
                              {item.label}
                            </span>
                            {': '}
                            {item.reason}
                          </li>
                        );
                      })
                    }
                  </ul>
                }
                position={tooltipPositions.LEFT}
              />
          }
        </TableRowCell>

        <TableRowCell className={styles.download}>
          {
            <SpinnerIconButton
              name={getDownloadIcon(isGrabbing, isGrabbed, grabError)}
              kind={getDownloadKind(isGrabbed, grabError, downloadAllowed)}
              title={getDownloadTooltip(isGrabbing, isGrabbed, grabError)}
              isSpinning={isGrabbing}
              onPress={alreadyGrabbed && !isGrabbed ? this.onRegrabPress : grabPress}
            />
          }
        </TableRowCell>

        <ConfirmModal
          isOpen={this.state.isConfirmGrabModalOpen}
          kind={kinds.WARNING}
          title={translate('GrabRelease')}
          message={translate('GrabReleaseMessageText', [title])}
          confirmLabel={translate('Grab')}
          onConfirm={this.onGrabConfirm}
          onCancel={this.onGrabCancel}
        />

        <ConfirmModal
          isOpen={this.state.isConfirmRegrabModalOpen}
          kind={kinds.WARNING}
          title={'Already grabbed'}
          message={getRegrabMessage(title, grabbedState, grabbedDownloadClient)}
          confirmLabel={'Grab anyway'}
          onConfirm={this.onRegrabConfirm}
          onCancel={this.onRegrabCancel}
        />
      </TableRow>
    );
  }
}

InteractiveSearchRow.propTypes = {
  rank: PropTypes.number.isRequired,
  guid: PropTypes.string.isRequired,
  protocol: PropTypes.string.isRequired,
  age: PropTypes.number.isRequired,
  ageHours: PropTypes.number.isRequired,
  ageMinutes: PropTypes.number.isRequired,
  publishDate: PropTypes.string.isRequired,
  title: PropTypes.string.isRequired,
  displayTitle: PropTypes.string,
  bookTitle: PropTypes.string,
  matchedTitleCharStart: PropTypes.number,
  matchedTitleCharEnd: PropTypes.number,
  infoUrl: PropTypes.string.isRequired,
  indexer: PropTypes.string.isRequired,
  indexerId: PropTypes.number.isRequired,
  size: PropTypes.number.isRequired,
  duration: PropTypes.string,
  narrator: PropTypes.string,
  seeders: PropTypes.number,
  leechers: PropTypes.number,
  quality: PropTypes.object.isRequired,
  customFormats: PropTypes.arrayOf(PropTypes.object).isRequired,
  customFormatScore: PropTypes.number.isRequired,
  indexerFlags: PropTypes.number.isRequired,
  isGraphicAudio: PropTypes.bool,
  isBundle: PropTypes.bool,
  isBundleFallback: PropTypes.bool,
  bundleContents: PropTypes.string,
  authorId: PropTypes.number,
  bookId: PropTypes.number,
  rejections: PropTypes.arrayOf(PropTypes.string).isRequired,
  rejectionDetails: PropTypes.arrayOf(PropTypes.object),
  downloadAllowed: PropTypes.bool.isRequired,
  isGrabbing: PropTypes.bool.isRequired,
  isGrabbed: PropTypes.bool.isRequired,
  isPreferredChoice: PropTypes.bool,
  alreadyGrabbed: PropTypes.bool,
  grabbedState: PropTypes.string,
  grabbedAt: PropTypes.string,
  grabbedDownloadClient: PropTypes.string,
  grabError: PropTypes.string,
  longDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  searchPayload: PropTypes.object.isRequired,
  onGrabPress: PropTypes.func.isRequired,
  formatType: PropTypes.string
};

InteractiveSearchRow.defaultProps = {
  indexerFlags: 0,
  rejections: [],
  rejectionDetails: [],
  isGrabbing: false,
  isGrabbed: false,
  isPreferredChoice: false,
  alreadyGrabbed: false,
  grabbedState: null,
  grabbedAt: null,
  grabbedDownloadClient: null,
  duration: null,
  narrator: null,
  isGraphicAudio: false,
  isBundle: false,
  isBundleFallback: false,
  bundleContents: null
};

export default InteractiveSearchRow;
