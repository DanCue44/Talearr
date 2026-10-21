import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import FormInputGroup from 'Components/Form/FormInputGroup';
import Label from 'Components/Label';
import IconButton from 'Components/Link/IconButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, inputTypes, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import AgeRatingFix from './AgeRatingFix';
import { ageRatingOptions, formatDate, formatSize, isUnrated, ratingLabel, readError, sourceLabels, stateKinds, stateLabels, volumeLabel } from './comicsFormat';
import ComicsReleasesModal from './ComicsReleasesModal';
import styles from './Comics.css';

const volumeColumns = [
  { name: 'monitored', label: '', isVisible: true },
  { name: 'number', label: 'Volume', isVisible: true },
  { name: 'title', label: 'Title', isVisible: true },
  { name: 'releaseDate', label: 'Released', isVisible: true },
  { name: 'state', label: 'Status', isVisible: true },
  { name: 'file', label: 'File', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

const historyColumns = [
  { name: 'date', label: 'Date', isVisible: true },
  { name: 'event', label: 'Event', isVisible: true },
  { name: 'details', label: 'Details', isVisible: true }
];

const eventLabels = {
  added: 'Added',
  grabbed: 'Grabbed',
  imported: 'Imported',
  importFailed: 'Import failed',
  downloadFailed: 'Download failed',
  fileDeleted: 'File removed',
  renamed: 'Renamed'
};

// Talearr: one comic or manga series: its volumes, what is downloaded, searches and history.
class ComicsSeriesPage extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      busy: null,
      error: null,
      message: null,
      detail: null,
      ageRatings: [],
      releasesVolume: null,
      isDeleteOpen: false
    };
  }

  componentDidMount() {
    this.fetchDetail();
    createAjaxRequest({ url: '/talearr/comics/status' }).request.done((status) => {
      this.setState({ ageRatings: status.ageRatings });
    });
  }

  componentDidUpdate(prevProps) {
    if (prevProps.match.params.seriesId !== this.props.match.params.seriesId) {
      this.fetchDetail();
    }
  }

  //
  // Data

  getSeriesId() {
    return this.props.match.params.seriesId;
  }

  fetchDetail = () => {
    const { request } = createAjaxRequest({ url: `/talearr/comics/series/${this.getSeriesId()}` });

    request.done((detail) => {
      this.setState({ isFetching: false, error: null, detail });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the series.') });
    });
  };

  run(busy, options, done) {
    this.setState({ busy, error: null, message: null });

    const { request } = createAjaxRequest({
      dataType: 'json',
      contentType: 'application/json',
      ...options
    });

    request.done((result) => {
      this.setState({ busy: null });
      if (done) {
        done(result);
      }

      this.fetchDetail();
    });

    request.fail((xhr) => {
      this.setState({ busy: null, error: readError(xhr, 'That did not work.') });
    });
  }

  //
  // Listeners

  onSeriesChange = (changes) => {
    this.run('save', { url: `/talearr/comics/series/${this.getSeriesId()}`, method: 'PUT', data: JSON.stringify(changes) });
  };

  onAgeRatingChange = ({ value }) => {
    this.onSeriesChange(value ? { ageRatingOverride: value } : { clearAgeRatingOverride: true });
  };

  onVolumeMonitorChange = (volume) => {
    this.run('monitor', {
      url: '/talearr/comics/volumes/monitor',
      method: 'PUT',
      data: JSON.stringify({ volumeIds: [volume.id], monitored: !volume.monitored })
    });
  };

  onGrabbed = () => {
    this.fetchDetail();
  };

  onRefreshPress = () => {
    this.run('refresh', { url: `/talearr/comics/series/${this.getSeriesId()}/refresh`, method: 'POST' });
  };

  onSearchPress = () => {
    this.run('search', { url: `/talearr/comics/series/${this.getSeriesId()}/search`, method: 'POST' }, (result) => {
      this.setState({ message: this.describeSearch(result) });
    });
  };

  onRescanPress = () => {
    this.run('rescan', { url: `/talearr/comics/series/${this.getSeriesId()}/rescan`, method: 'POST' }, (result) => {
      this.setState({ message: result.imported.length ? `Found ${result.imported.length} file(s) in the folder.` : 'No new files in the folder.' });
    });
  };

  onRenamePress = () => {
    this.run('rename', { url: `/talearr/comics/series/${this.getSeriesId()}/rename`, method: 'POST' }, (result) => {
      this.setState({ message: result.imported.length ? `Renamed ${result.imported.length} file(s).` : 'Every file already has the right name.' });
    });
  };

  onVolumeSearchPress = (volume) => {
    this.run(`search-${volume.id}`, { url: `/talearr/comics/volumes/${volume.id}/search`, method: 'POST' }, (result) => {
      this.setState({ message: this.describeSearch(result) });
    });
  };

  onDeletePress = () => {
    this.setState({ isDeleteOpen: true });
  };

  onDeleteConfirm = () => {
    const { request } = createAjaxRequest({ url: `/talearr/comics/series/${this.getSeriesId()}?deleteFiles=false`, method: 'DELETE' });

    request.done(() => {
      this.props.history.push('/comics');
    });

    request.fail((xhr) => {
      this.setState({ isDeleteOpen: false, error: readError(xhr, 'Unable to remove the series.') });
    });
  };

  describeSearch(result) {
    const notes = result.notices && result.notices.length ? ` ${result.notices.join(' ')}` : '';
    if (result.grabbed) {
      return `Sent ${result.grabbed} release(s) to the download client.${notes}`;
    }

    return `Nothing suitable found (${result.releases.length} releases looked at).${notes}`;
  }

  //
  // Render

  render() {
    const { isFetching, busy, error, message, detail, ageRatings, releasesVolume, isDeleteOpen } = this.state;

    if (isFetching && !detail) {
      return (
        <PageContent title="Comics">
          <PageContentBody>
            <LoadingIndicator />
          </PageContentBody>
        </PageContent>
      );
    }

    if (!detail) {
      return (
        <PageContent title="Comics">
          <PageContentBody>
            <Alert kind={kinds.DANGER}>{error}</Alert>
          </PageContentBody>
        </PageContent>
      );
    }

    const { series, volumes, history, effectiveAgeRating, sizeOnDisk } = detail;

    return (
      <PageContent title={series.title}>
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label="Refresh"
              iconName={icons.REFRESH}
              isSpinning={busy === 'refresh'}
              onPress={this.onRefreshPress}
            />

            <PageToolbarButton
              label="Search Missing"
              iconName={icons.SEARCH}
              isSpinning={busy === 'search'}
              onPress={this.onSearchPress}
            />

            <PageToolbarButton
              label="Rescan Folder"
              iconName={icons.RESCAN}
              isSpinning={busy === 'rescan'}
              onPress={this.onRescanPress}
            />

            <PageToolbarButton
              label="Rename Files"
              iconName={icons.ORGANIZE}
              isSpinning={busy === 'rename'}
              onPress={this.onRenamePress}
            />

            <PageToolbarButton
              label="Remove"
              iconName={icons.DELETE}
              onPress={this.onDeletePress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <div className={styles.header}>
            {
              series.coverUrl ?
                <img
                  className={styles.headerCover}
                  src={series.coverUrl}
                  alt=""
                /> :
                null
            }

            <div className={styles.headerInfo}>
              <h1 className={styles.title}>{series.title}</h1>
              {series.alternateTitles ? <div className={styles.alternate}>{series.alternateTitles.split('\n').join(' / ')}</div> : null}

              <div className={styles.badges}>
                <Label kind={kinds.INFO}>{series.kind === 'comic' ? 'Comic' : 'Manga'}</Label>
                {series.year ? <Label>{series.year}</Label> : null}
                {series.status ? <Label>{series.status}</Label> : null}
                {series.kind === 'manga' ? <Label>{series.readingDirection === 'ltr' ? 'Left to right' : 'Right to left'}</Label> : null}
                <Label kind={isUnrated(effectiveAgeRating) ? kinds.WARNING : kinds.DEFAULT}>{`Age: ${ratingLabel(effectiveAgeRating)}`}</Label>
                {sizeOnDisk ? <Label>{formatSize(sizeOnDisk)}</Label> : null}
              </div>

              {series.authors ? <div className={styles.small}>{series.authors}</div> : null}
              <div className={styles.small}>{series.path}</div>
              {series.overview ? <div className={styles.overview}>{series.overview}</div> : null}

              <div className={styles.controls}>
                <label className={styles.inline}>
                  <CheckInput
                    containerClassName={styles.check}
                    name="monitored"
                    value={series.monitored}
                    onChange={({ value }) => this.onSeriesChange({ monitored: value })}
                  />
                  {'Monitored'}
                </label>

                <label className={styles.inline}>
                  <CheckInput
                    containerClassName={styles.check}
                    name="monitorNewVolumes"
                    value={series.monitorNewVolumes}
                    onChange={({ value }) => this.onSeriesChange({ monitorNewVolumes: value })}
                  />
                  {'Monitor new volumes'}
                </label>

                <div className={styles.inline}>
                  {'Age rating'}
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="ageRatingOverride"
                    values={ageRatingOptions(ageRatings, `From the data (${ratingLabel(series.ageRating)})`)}
                    value={series.ageRatingOverride || ''}
                    onChange={this.onAgeRatingChange}
                  />
                </div>
              </div>
            </div>
          </div>

          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}

          {
            isUnrated(effectiveAgeRating) ?
              <Alert kind={kinds.WARNING} className={styles.unrated}>
                <div>
                  {detail.inKidsFolder ?
                    'This series has no age rating, so its kids library will not take new volumes and readers cannot apply an age limit. ' :
                    'This series has no age rating, so readers cannot apply an age limit to it. '}
                </div>
                <AgeRatingFix
                  seriesId={series.id}
                  ageRatings={ageRatings}
                  onSaved={this.onGrabbed}
                />
              </Alert> :
              null
          }
          {message ? <Alert kind={kinds.INFO}>{message}</Alert> : null}

          {
            !volumes.length ?
              <Alert kind={kinds.WARNING}>
                {'No volume list was found for this series. Refresh later, or check that a Hardcover API token is set in Settings > Metadata.'}
              </Alert> :
              null
          }

          {
            volumes.length ?
              <Table columns={volumeColumns}>
                <TableBody>
                  {
                    volumes.map((volume) => {
                      return (
                        <TableRow key={volume.id}>
                          <TableRowCell>
                            <IconButton
                              name={volume.monitored ? icons.MONITORED : icons.UNMONITORED}
                              title={volume.monitored ? 'Monitored: click to stop' : 'Not monitored: click to monitor'}
                              onPress={() => this.onVolumeMonitorChange(volume)}
                            />
                          </TableRowCell>
                          <TableRowCell>{volumeLabel(volume.number)}</TableRowCell>
                          <TableRowCell>{volume.title}</TableRowCell>
                          <TableRowCell>{formatDate(volume.releaseDate)}</TableRowCell>
                          <TableRowCell>
                            <Label kind={stateKinds[volume.state]}>{stateLabels[volume.state] || volume.state}</Label>
                          </TableRowCell>
                          <TableRowCell>
                            {
                              volume.file ?
                                <span className={styles.small} title={volume.file.path}>
                                  {`${(volume.file.format || '').toUpperCase()}, ${sourceLabels[volume.file.source] || volume.file.source}, ${formatSize(volume.file.size)}`}
                                </span> :
                                null
                            }
                          </TableRowCell>
                          <TableRowCell>
                            <div className={styles.actions}>
                              <IconButton
                                name={icons.SEARCH}
                                title="Automatic search"
                                isSpinning={busy === `search-${volume.id}`}
                                onPress={() => this.onVolumeSearchPress(volume)}
                              />
                              <IconButton
                                name={icons.INTERACTIVE}
                                title="Interactive search"
                                onPress={() => this.setState({ releasesVolume: volume })}
                              />
                            </div>
                          </TableRowCell>
                        </TableRow>
                      );
                    })
                  }
                </TableBody>
              </Table> :
              null
          }

          {
            history && history.length ?
              <div className={styles.section}>
                <div className={styles.sectionTitle}>{'History'}</div>
                <Table columns={historyColumns}>
                  <TableBody>
                    {
                      history.map((entry) => {
                        return (
                          <TableRow key={entry.id}>
                            <TableRowCell>{new Date(entry.date).toLocaleString()}</TableRowCell>
                            <TableRowCell>{eventLabels[entry.eventType] || entry.eventType}</TableRowCell>
                            <TableRowCell>
                              {entry.sourceTitle ? <div>{entry.sourceTitle}</div> : null}
                              {entry.message ? <div className={styles.small}>{entry.message}</div> : null}
                            </TableRowCell>
                          </TableRow>
                        );
                      })
                    }
                  </TableBody>
                </Table>
              </div> :
              null
          }
        </PageContentBody>

        <ComicsReleasesModal
          isOpen={!!releasesVolume}
          volume={releasesVolume}
          seriesTitle={series.title}
          onGrabbed={this.onGrabbed}
          onModalClose={() => this.setState({ releasesVolume: null })}
        />

        <ConfirmModal
          isOpen={isDeleteOpen}
          kind={kinds.DANGER}
          title={`Remove ${series.title}`}
          message="Talearr stops tracking this series. Its files stay on disk."
          confirmLabel="Remove"
          isSpinning={false}
          onConfirm={this.onDeleteConfirm}
          onCancel={() => this.setState({ isDeleteOpen: false })}
        />
      </PageContent>
    );
  }
}

ComicsSeriesPage.propTypes = {
  match: PropTypes.object.isRequired,
  history: PropTypes.object.isRequired
};

export default ComicsSeriesPage;
