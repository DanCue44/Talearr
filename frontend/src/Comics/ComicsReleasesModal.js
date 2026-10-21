import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { formatSize, readError, sourceLabels, volumeLabel } from './comicsFormat';
import styles from './Comics.css';

const columns = [
  { name: 'title', label: 'Release', isVisible: true },
  { name: 'source', label: 'Source', isVisible: true },
  { name: 'size', label: 'Size', isVisible: true },
  { name: 'age', label: 'Age', isVisible: true },
  { name: 'indexer', label: 'Indexer', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

// Talearr: every release the indexers have for one volume, with why each would or would not be
// taken, and a button to grab one by hand.
class ComicsReleasesModal extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: false,
      grabbing: null,
      grabbed: {},
      error: null,
      notices: [],
      releases: []
    };
  }

  componentDidUpdate(prevProps) {
    if (this.props.isOpen && !prevProps.isOpen) {
      this.search();
    }
  }

  //
  // Data

  search = () => {
    this.setState({ isFetching: true, error: null, releases: [], notices: [], grabbed: {} });

    const { request } = createAjaxRequest({ url: `/talearr/comics/volumes/${this.props.volume.id}/releases` });

    request.done((result) => {
      this.setState({ isFetching: false, releases: result.releases, notices: result.notices });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Search failed.') });
    });
  };

  onSearchAgainPress = () => {
    this.search();
  };

  onGrabPress = (release) => {
    const key = `${release.indexerId}|${release.guid}`;
    this.setState({ grabbing: key, error: null });

    const { request } = createAjaxRequest({
      url: '/talearr/comics/releases/grab',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ guid: release.guid, indexerId: release.indexerId })
    });

    request.done(() => {
      this.setState({ grabbing: null, grabbed: { ...this.state.grabbed, [key]: true } });
      this.props.onGrabbed();
    });

    request.fail((xhr) => {
      this.setState({ grabbing: null, error: readError(xhr, 'Unable to send the release to the download client.') });
    });
  };

  //
  // Render

  render() {
    const { isOpen, volume, seriesTitle, onModalClose } = this.props;
    const { isFetching, grabbing, grabbed, error, notices, releases } = this.state;

    return (
      <Modal isOpen={isOpen} size={sizes.EXTRA_EXTRA_LARGE}
        onModalClose={onModalClose}
      >
        <ModalContent onModalClose={onModalClose}>
          <ModalHeader>
            {volume ? `${seriesTitle} ${volumeLabel(volume.number)}` : 'Search'}
          </ModalHeader>

          <ModalBody>
            {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}
            {
              notices.map((notice) => {
                return (
                  <Alert
                    key={notice}
                    kind={kinds.WARNING}
                    className={styles.notice}
                  >
                    {notice}
                  </Alert>
                );
              })
            }
            {isFetching ? <LoadingIndicator /> : null}
            {!isFetching && !error && !releases.length ? <Alert kind={kinds.INFO}>{'The indexers have nothing for this volume.'}</Alert> : null}

            {
              releases.length ?
                <Table columns={columns}>
                  <TableBody>
                    {
                      releases.map((release) => {
                        const key = `${release.indexerId}|${release.guid}`;

                        return (
                          <TableRow key={key}>
                            <TableRowCell>
                              <div>{release.title}</div>
                              {
                                release.accepted ?
                                  <div className={styles.small}>
                                    {release.volumes && release.volumes.length ? release.volumes.map(volumeLabel).join(', ') : ''}
                                    {release.group ? ` from ${release.group}` : ''}
                                  </div> :
                                  <div className={styles.rejections}>{release.rejections.join('. ')}</div>
                              }
                            </TableRowCell>

                            <TableRowCell>
                              <Label kind={release.source === 'digital' ? kinds.SUCCESS : kinds.DEFAULT}>{sourceLabels[release.source] || release.source}</Label>
                              {release.format !== 'unknown' ? <Label>{release.format.toUpperCase()}</Label> : null}
                            </TableRowCell>

                            <TableRowCell>{formatSize(release.size)}</TableRowCell>
                            <TableRowCell>{`${release.ageDays} d`}</TableRowCell>
                            <TableRowCell>
                              {release.indexer}
                              {release.seeders != null ? <div className={styles.small}>{`${release.seeders} seeders`}</div> : null}
                            </TableRowCell>

                            <TableRowCell>
                              {
                                grabbed[key] ?
                                  <Label kind={kinds.SUCCESS}>{'Sent'}</Label> :
                                  <SpinnerButton
                                    kind={release.accepted ? kinds.PRIMARY : kinds.DEFAULT}
                                    isSpinning={grabbing === key}
                                    isDisabled={!release.volumes || !release.volumes.length}
                                    title={release.volumes && release.volumes.length ? 'Send to the download client' : 'Talearr cannot tell which volume this is'}
                                    onPress={() => this.onGrabPress(release)}
                                  >
                                    {'Grab'}
                                  </SpinnerButton>
                              }
                            </TableRowCell>
                          </TableRow>
                        );
                      })
                    }
                  </TableBody>
                </Table> :
                null
            }
          </ModalBody>

          <ModalFooter>
            <Button onPress={this.onSearchAgainPress}>{'Search again'}</Button>
            <Button onPress={onModalClose}>{'Close'}</Button>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

ComicsReleasesModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  volume: PropTypes.object,
  seriesTitle: PropTypes.string,
  onGrabbed: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default ComicsReleasesModal;
