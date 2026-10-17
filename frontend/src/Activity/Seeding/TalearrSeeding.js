import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import ProgressBar from 'Components/ProgressBar';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import formatBytes from 'Utilities/Number/formatBytes';
import styles from './TalearrSeeding.css';

const REFRESH_INTERVAL = 60 * 1000;

const mediaTypeLabels = {
  audiobook: 'Audiobook',
  ebook: 'Ebook'
};

const columns = [
  { name: 'book', label: 'Book', isVisible: true },
  { name: 'author', label: 'Author', isVisible: true },
  { name: 'format', label: 'Format', isVisible: true },
  { name: 'indexer', label: 'Indexer', isVisible: true },
  { name: 'state', label: 'State', isVisible: true },
  { name: 'seeded', label: 'Seeded', isVisible: true },
  { name: 'uploaded', label: 'Uploaded / Ratio', isVisible: true },
  { name: 'risk', label: 'Risk', isVisible: true }
];

const stateKinds = {
  seeding: kinds.SUCCESS,
  stalled: kinds.WARNING,
  queued: kinds.DEFAULT,
  paused: kinds.WARNING,
  checking: kinds.INFO,
  error: kinds.DANGER,
  downloading: kinds.INFO,
  missing: kinds.DANGER
};

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}

export function formatSeconds(seconds) {
  if (seconds == null) {
    return '-';
  }

  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);

  if (days > 0) {
    return hours % 24 ? `${days}d ${hours % 24}h` : `${days}d`;
  }

  if (hours > 0) {
    return minutes % 60 ? `${hours}h ${minutes % 60}m` : `${hours}h`;
  }

  return `${minutes}m`;
}

function Stat({ label, value }) {
  return (
    <div className={styles.stat}>
      <span className={styles.statLabel}>{label}</span>
      <span className={styles.statValue}>{value == null ? '-' : value}</span>
    </div>
  );
}

Stat.propTypes = {
  label: PropTypes.string.isRequired,
  value: PropTypes.oneOfType([PropTypes.string, PropTypes.number])
};

function SeededCell({ torrent }) {
  const { seedingSeconds, requiredSeedingSeconds, seedGoalSeconds, requirementMet } = torrent;
  const target = requiredSeedingSeconds || seedGoalSeconds;

  if (!target || seedingSeconds == null) {
    return <span>{formatSeconds(seedingSeconds)}</span>;
  }

  const progress = Math.min(100, (seedingSeconds / target) * 100);
  const goal = seedGoalSeconds && seedGoalSeconds !== target ? `, goal ${formatSeconds(seedGoalSeconds)}` : '';

  return (
    <ProgressBar
      progress={progress}
      kind={requirementMet === false ? kinds.PRIMARY : kinds.SUCCESS}
      size={sizes.SMALL}
      showText={true}
      text={`${formatSeconds(seedingSeconds)} / ${formatSeconds(target)}`}
      title={`Seeded ${formatSeconds(seedingSeconds)} of the required ${formatSeconds(target)}${goal}`}
    />
  );
}

SeededCell.propTypes = {
  torrent: PropTypes.object.isRequired
};

function RiskCell({ torrent }) {
  if (torrent.atRisk) {
    return (
      <div>
        <Label kind={kinds.DANGER}>{'At risk'}</Label>
        <div className={styles.reason}>{torrent.riskReason}</div>
      </div>
    );
  }

  if (torrent.isPrivateTracker && torrent.requirementMet === true) {
    return <Label kind={kinds.SUCCESS}>{'Satisfied'}</Label>;
  }

  if (torrent.isPrivateTracker && torrent.requirementMet === false) {
    return <Label kind={kinds.INFO}>{'Seeding to goal'}</Label>;
  }

  return <Label kind={kinds.DEFAULT}>{torrent.isPrivateTracker ? 'Unknown' : 'Public'}</Label>;
}

RiskCell.propTypes = {
  torrent: PropTypes.object.isRequired
};

// TaleArr: Activity > Seeding. What the torrent clients seed for TaleArr, with the private-tracker
// seeding requirement and hit-and-run risk. Read-only.
class TalearrSeeding extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._abortRequest = null;
    this._timer = null;

    this.state = {
      isFetching: true,
      error: null,
      report: null
    };
  }

  componentDidMount() {
    this.fetchReport();
    this._timer = setInterval(this.fetchReport, REFRESH_INTERVAL);
  }

  componentWillUnmount() {
    clearInterval(this._timer);

    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  //
  // Data

  fetchReport = () => {
    if (this._abortRequest) {
      this._abortRequest();
    }

    this.setState({ isFetching: true });

    const { request, abortRequest } = createAjaxRequest({
      url: '/talearr/seeding'
    });

    this._abortRequest = abortRequest;

    request.done((data) => {
      this.setState({ isFetching: false, error: null, report: data });
    });

    request.fail((xhr) => {
      if (!xhr.aborted) {
        this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the seeding status.') });
      }
    });
  };

  //
  // Listeners

  onRefreshPress = () => {
    this.fetchReport();
  };

  //
  // Render

  renderMam(mam) {
    if (!mam) {
      return null;
    }

    const unsatisfied = mam.unsatisfied == null ?
      null :
      `${mam.unsatisfied}${mam.unsatisfiedLimit == null ? '' : ` / ${mam.unsatisfiedLimit}`}`;

    const bonus = mam.bonusPoints == null ? null : Math.floor(mam.bonusPoints).toLocaleString();
    const checked = mam.checkedAt ? new Date(mam.checkedAt).toLocaleString() : 'not yet';

    return (
      <div className={styles.mam} title={`MyAnonamouse account status, last checked ${checked}`}>
        <Stat label="MAM class" value={mam.userClass ? `${mam.userClass}${mam.isVip ? ' (VIP)' : ''}` : null} />
        <Stat label="Unsatisfied" value={unsatisfied} />
        <Stat label="Wedges" value={mam.wedges} />
        <Stat label="Bonus points" value={bonus} />
        <Stat label="Checked" value={checked} />
      </div>
    );
  }

  renderRow(torrent) {
    const name = torrent.bookTitle || torrent.title || torrent.downloadId;

    return (
      <TableRow key={`${torrent.downloadClient}:${torrent.downloadId}`}>
        <TableRowCell>
          <div>{name}</div>
          {
            torrent.bookTitle && torrent.title ?
              <div className={styles.release}>{torrent.title}</div> :
              null
          }
        </TableRowCell>
        <TableRowCell>{torrent.authorName || '-'}</TableRowCell>
        <TableRowCell>{mediaTypeLabels[torrent.mediaType] || '-'}</TableRowCell>
        <TableRowCell>
          {torrent.indexer || '-'}
          {torrent.isPrivateTracker ? <Label kind={kinds.INVERSE} size={sizes.SMALL}>{'Private'}</Label> : null}
        </TableRowCell>
        <TableRowCell>
          <Label kind={stateKinds[torrent.state] || kinds.DEFAULT} title={torrent.downloadClient || undefined}>
            {torrent.state}
          </Label>
        </TableRowCell>
        <TableRowCell className={styles.seeded}>
          <SeededCell torrent={torrent} />
        </TableRowCell>
        <TableRowCell>
          {torrent.uploaded == null ? '-' : formatBytes(torrent.uploaded)}
          {torrent.ratio == null ? '' : ` / ${torrent.ratio.toFixed(2)}`}
        </TableRowCell>
        <TableRowCell>
          <RiskCell torrent={torrent} />
        </TableRowCell>
      </TableRow>
    );
  }

  render() {
    const { isFetching, error, report } = this.state;
    const torrents = (report && report.torrents) || [];
    const summary = report && report.summary;

    return (
      <PageContent title="Seeding">
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label="Refresh"
              iconName={icons.REFRESH}
              isSpinning={isFetching}
              onPress={this.onRefreshPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <div className={styles.intro}>
            {
              'Torrents your download clients are seeding for TaleArr. Private trackers need a minimum ' +
              'seeding time (MyAnonamouse: 72 hours within 30 days); a torrent that stops before that ' +
              'counts as a hit and run. Times are refreshed every minute.'
            }
          </div>

          {report ? this.renderMam(report.mam) : null}

          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}

          {!report && isFetching ? <LoadingIndicator /> : null}

          {
            summary ?
              <div className={styles.summary}>
                {
                  `${summary.total} torrents, ${summary.seeding} seeding, ` +
                  `${summary.privateUnsatisfied} private still seeding to their requirement `
                }
                {summary.atRisk ? <Label kind={kinds.DANGER}>{`${summary.atRisk} at risk`}</Label> : null}
              </div> :
              null
          }

          {
            report && !torrents.length ?
              <Alert kind={kinds.INFO}>{'No torrents in TaleArr\'s download client categories.'}</Alert> :
              null
          }

          {
            torrents.length ?
              <Table columns={columns}>
                <TableBody>
                  {torrents.map((t) => this.renderRow(t))}
                </TableBody>
              </Table> :
              null
          }
        </PageContentBody>
      </PageContent>
    );
  }
}

export default TalearrSeeding;
