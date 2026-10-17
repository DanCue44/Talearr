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
    const ratio = mam.ratio == null ? null : mam.ratio.toFixed(2);
    const vipUntil = mam.vipUntil ? new Date(mam.vipUntil).toLocaleDateString() : null;

    return (
      <div>
        <div className={styles.mam} title={`MyAnonamouse account status, last checked ${checked}`}>
          <Stat label="MAM class" value={mam.userClass ? `${mam.userClass}${mam.isVip ? ' (VIP)' : ''}` : null} />
          {vipUntil ? <Stat label="VIP until" value={vipUntil} /> : null}
          {ratio ? <Stat label="Ratio" value={ratio} /> : null}
          <Stat label="Unsatisfied" value={unsatisfied} />
          <Stat label="Wedges" value={mam.wedges} />
          <Stat label="Bonus points" value={bonus} />
          <Stat label="Checked" value={checked} />
        </div>

        {
          mam.restricted ?
            <Alert kind={kinds.WARNING}>
              {
                'MAM limited this account (Mouse or seed-only) because the ratio is too low. Buy upload ' +
                'credit in the Bonus Points Store (API minimum 50 GB = 25,000 points).'
              }
            </Alert> :
            null
        }

        {this.renderBonusPurchases(mam)}

        {this.renderPowerUser(mam)}
      </div>
    );
  }

  // TaleArr: MAM promotes to Power User after 4 weeks, over 25 GiB uploaded and ratio 2.0.
  renderPowerUser(mam) {
    const known = mam.daysUntilEligible != null || mam.uploadedRemainingBytes != null || mam.ratioOk != null;

    if (!known || (mam.userClass && (/power user|vip|elite|star|staff|mod/i).test(mam.userClass))) {
      return null;
    }

    let membership = 'Join date unknown';

    if (mam.daysUntilEligible != null) {
      membership = mam.daysUntilEligible === 0 ?
        'Member 4 weeks: done' :
        `Member 4 weeks: ${mam.daysUntilEligible} day${mam.daysUntilEligible === 1 ? '' : 's'} to go`;
    }

    let upload = 'Uploaded over 25 GiB: unknown';

    if (mam.uploadedRemainingBytes != null) {
      upload = mam.uploadedRemainingBytes === 0 ?
        'Uploaded over 25 GiB: done' :
        `Uploaded over 25 GiB: ${formatBytes(mam.uploadedRemainingBytes)} to go`;
    }

    let ratio = 'Ratio 2.0: unknown';

    if (mam.ratioOk != null) {
      ratio = mam.ratioOk ? 'Ratio 2.0: ok' : `Ratio 2.0: not yet${mam.ratio == null ? '' : ` (${mam.ratio.toFixed(2)})`}`;
    }

    return (
      <div className={styles.bonus} title="MAM promotes to Power User automatically within 15 minutes of meeting all three; below ratio 2.0 it demotes again">
        <div className={styles.bonusTitle}>{'Power User progress'}</div>
        <ul className={styles.bonusList}>
          <li>{membership}</li>
          <li>{upload}</li>
          <li>{ratio}</li>
        </ul>
      </div>
    );
  }

  // TaleArr: the freeleech seeding filler's last run and what it would grab (or grabbed).
  renderFiller(filler) {
    if (!filler || (!filler.enabled && !(filler.preview || []).length)) {
      return null;
    }

    const preview = filler.preview || [];
    const lastRun = filler.lastRunAt ? new Date(filler.lastRunAt).toLocaleString() : 'not yet';

    return (
      <div className={styles.bonus}>
        <div className={styles.bonusTitle}>
          {'Filler preview'}
          {filler.dryRun ? <Label kind={kinds.INFO} size={sizes.SMALL}>{'Dry run'}</Label> : null}
        </div>

        <div className={styles.bonusReason}>
          {`Last run ${lastRun}: ${filler.lastResult || '-'}`}
          {filler.target == null ? '' : ` Target ${filler.target}, free ${filler.freeSlots}, seeding ${filler.tracked}.`}
        </div>

        {
          preview.length ?
            <ul className={styles.bonusList}>
              {
                preview.map((p) => {
                  let status = filler.previewIsDryRun ? 'Would grab' : 'Skipped';
                  let kind = kinds.DEFAULT;

                  if (p.grabbed) {
                    status = 'Grabbed';
                    kind = kinds.SUCCESS;
                  }

                  // TaleArr: taste preferences (Preferred badge, why, and the library import).
                  const details = [
                    p.preferredReason,
                    p.preferred || !p.category ? null : p.category,
                    p.libraryMessage ? `Library: ${p.libraryMessage}` : null
                  ].filter(Boolean).join('. ');

                  return (
                    <li key={p.torrentId}>
                      <Label kind={kind} size={sizes.SMALL}>{status}</Label>
                      {
                        p.preferred ?
                          <Label kind={kinds.PRIMARY} size={sizes.SMALL}>{'Preferred'}</Label> :
                          null
                      }
                      {` ${p.title}${p.author ? ` by ${p.author}` : ''} (${p.isAudiobook ? 'audiobook' : 'ebook'}, ${formatBytes(p.size)}, `}
                      {`${p.leechers} leechers, ${p.seeders} seeders)`}
                      {details ? <div className={styles.bonusReason}>{details}</div> : null}
                    </li>
                  );
                })
              }
            </ul> :
            null
        }
      </div>
    );
  }

  renderBonusPurchases(mam) {
    const purchases = mam.bonusPurchases || [];

    if (!mam.bonusAutoSpend && !purchases.length) {
      return null;
    }

    return (
      <div className={styles.bonus}>
        <div className={styles.bonusTitle}>
          {'Recent bonus purchases'}
          {mam.bonusDryRun ? <Label kind={kinds.INFO} size={sizes.SMALL}>{'Dry run'}</Label> : null}
        </div>

        {
          purchases.length ?
            <ul className={styles.bonusList}>
              {
                purchases.map((p) => {
                  let kind = p.success ? kinds.SUCCESS : kinds.DANGER;
                  let status = p.success ? 'Bought' : 'Failed';

                  if (p.dryRun) {
                    kind = kinds.DEFAULT;
                    status = 'Would buy';
                  }

                  return (
                    <li key={`${p.attemptedAt}-${p.parameters}`} title={p.message || undefined}>
                      <Label kind={kind} size={sizes.SMALL}>{status}</Label>
                      {` ${new Date(p.attemptedAt).toLocaleString()}: ${p.parameters}`}
                      {` (about ${p.estimatedCost.toLocaleString()} points)`}
                      {p.reason ? <span className={styles.bonusReason}>{` ${p.reason}`}</span> : null}
                      {!p.success && !p.dryRun && p.message ? <span className={styles.bonusReason}>{` - ${p.message}`}</span> : null}
                    </li>
                  );
                })
              }
            </ul> :
            <div className={styles.bonusReason}>{'None yet. Auto-spend is on for this indexer.'}</div>
        }
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
          {
            torrent.isFiller ?
              <span title="Added by the MAM freeleech seeding filler">
                <Label kind={kinds.INFO} size={sizes.SMALL}>{'Filler'}</Label>
              </span> :
              null
          }
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

          {report ? this.renderFiller(report.filler) : null}

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
