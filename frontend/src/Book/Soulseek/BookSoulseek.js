import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import formatBytes from 'Utilities/Number/formatBytes';
import styles from './BookSoulseek.css';

const resultColumns = [
  { name: 'title', label: 'Folder', isVisible: true },
  { name: 'peer', label: 'Peer', isVisible: true },
  { name: 'files', label: 'Files', isVisible: true },
  { name: 'size', label: 'Size', isVisible: true },
  { name: 'decision', label: 'Decision', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

const grabColumns = [
  { name: 'title', label: 'Soulseek download', isVisible: true },
  { name: 'peer', label: 'Peer', isVisible: true },
  { name: 'state', label: 'State', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

const REFRESH_MS = 15000;

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}

function stateKind(state) {
  switch (state) {
    case 'imported':
      return kinds.SUCCESS;
    case 'failed':
    case 'importfailed':
      return kinds.DANGER;
    case 'cancelled':
      return kinds.DISABLED;
    default:
      return kinds.INFO;
  }
}

function postJson(url, data) {
  return createAjaxRequest({
    url,
    method: 'POST',
    dataType: 'json',
    contentType: 'application/json',
    data: JSON.stringify(data || {})
  }).request;
}

// TaleArr: Search Soulseek for this book through slskd and grab a whole folder
// (docs/TALEARR_SOULSEEK.md). Hidden unless Soulseek is turned on in Settings > Download Clients.
class BookSoulseek extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._timer = null;

    this.state = {
      enabled: false,
      grabs: [],
      search: null,
      isSearching: false,
      error: null,
      grabbingKey: null
    };
  }

  componentDidMount() {
    const { request } = createAjaxRequest({ url: '/talearr/soulseek/status' });

    request.done((status) => {
      if (status.enabled) {
        this.setState({ enabled: true });
      }

      this.fetchGrabs();
    });
  }

  componentDidUpdate(prevProps) {
    if (prevProps.bookId !== this.props.bookId) {
      this.setState({ search: null, error: null, grabs: [] });
      this.fetchGrabs();
    }
  }

  componentWillUnmount() {
    this.clearTimer();
  }

  //
  // Data

  clearTimer() {
    if (this._timer) {
      clearTimeout(this._timer);
      this._timer = null;
    }
  }

  fetchGrabs = () => {
    this.clearTimer();

    const { request } = createAjaxRequest({
      url: '/talearr/soulseek/grabs',
      data: { bookId: this.props.bookId }
    });

    request.done((grabs) => {
      this.setState({ grabs });

      if (grabs.some((g) => g.isActive)) {
        this._timer = setTimeout(this.fetchGrabs, REFRESH_MS);
      }
    });
  };

  //
  // Listeners

  onSearchPress = () => {
    this.setState({ isSearching: true, error: null, search: null });

    const request = postJson('/talearr/soulseek/search', { bookId: this.props.bookId });

    request.done((search) => {
      this.setState({ isSearching: false, search });
    });

    request.fail((xhr) => {
      this.setState({ isSearching: false, error: readError(xhr, 'The Soulseek search failed.') });
    });
  };

  onGrabPress = (key) => {
    const { search } = this.state;
    this.setState({ grabbingKey: key, error: null });

    const request = postJson('/talearr/soulseek/grab', { token: search.token, key });

    request.done(() => {
      this.setState({ grabbingKey: null });
      this.fetchGrabs();
    });

    request.fail((xhr) => {
      this.setState({ grabbingKey: null, error: readError(xhr, 'The download could not be started.') });
    });
  };

  onCancelPress = (id) => {
    postJson(`/talearr/soulseek/grabs/${id}/cancel`).always(this.fetchGrabs);
  };

  onForgetPress = (id) => {
    createAjaxRequest({ url: `/talearr/soulseek/grabs/${id}`, method: 'DELETE' }).request.always(this.fetchGrabs);
  };

  //
  // Render

  renderGrabs() {
    const { grabs } = this.state;

    if (!grabs.length) {
      return null;
    }

    return (
      <Table columns={grabColumns}>
        <TableBody>
          {
            grabs.map((grab) => {
              return (
                <TableRow key={grab.id}>
                  <TableRowCell>
                    {grab.title}
                    {grab.message ? <div className={styles.path}>{grab.message}</div> : null}
                  </TableRowCell>
                  <TableRowCell>{grab.username}</TableRowCell>
                  <TableRowCell>
                    <Label kind={stateKind(grab.state)}>{grab.state}</Label>
                  </TableRowCell>
                  <TableRowCell>
                    {
                      grab.isActive ?
                        <Button size={sizes.SMALL} onPress={() => this.onCancelPress(grab.id)}>{'Cancel'}</Button> :
                        <Button size={sizes.SMALL} onPress={() => this.onForgetPress(grab.id)}>{'Forget'}</Button>
                    }
                  </TableRowCell>
                </TableRow>
              );
            })
          }
        </TableBody>
      </Table>
    );
  }

  renderResults() {
    const { search, grabbingKey } = this.state;

    if (!search) {
      return null;
    }

    if (!search.results.length) {
      return (
        <Alert kind={kinds.INFO}>
          {`Nothing usable on Soulseek for "${search.searchText}" (${search.peerCount} peers answered).`}
        </Alert>
      );
    }

    return (
      <Table columns={resultColumns}>
        <TableBody>
          {
            search.results.map((result) => {
              return (
                <TableRow key={result.key}>
                  <TableRowCell>
                    {result.title}
                    <div className={styles.path}>{result.directory}</div>
                  </TableRowCell>
                  <TableRowCell>
                    {result.username}
                    <div className={styles.path}>
                      {result.hasFreeUploadSlot ? 'free slot' : `queue ${result.queueLength}`}
                      {result.uploadSpeed ? `, ${formatBytes(result.uploadSpeed)}/s` : ''}
                    </div>
                  </TableRowCell>
                  <TableRowCell>{result.fileCount}</TableRowCell>
                  <TableRowCell>{formatBytes(result.size)}</TableRowCell>
                  <TableRowCell>
                    <div
                      className={styles.labels}
                      title={result.rejections.join('\n')}
                    >
                      {
                        result.approved ?
                          <Label kind={kinds.SUCCESS}>{result.quality || 'OK'}</Label> :
                          result.rejectionLabels.map((label) => {
                            return (
                              <Label
                                key={label}
                                kind={kinds.DANGER}
                              >
                                {label}
                              </Label>
                            );
                          })
                      }
                    </div>
                  </TableRowCell>
                  <TableRowCell>
                    <SpinnerButton
                      size={sizes.SMALL}
                      kind={result.approved ? kinds.PRIMARY : kinds.DEFAULT}
                      isSpinning={grabbingKey === result.key}
                      isDisabled={!!grabbingKey}
                      onPress={() => this.onGrabPress(result.key)}
                    >
                      {'Grab'}
                    </SpinnerButton>
                  </TableRowCell>
                </TableRow>
              );
            })
          }
        </TableBody>
      </Table>
    );
  }

  render() {
    const { enabled, grabs, isSearching, error } = this.state;

    if (!enabled && !grabs.length) {
      return null;
    }

    return (
      <div className={styles.container}>
        <div className={styles.header}>
          <div className={styles.title}>{'Soulseek'}</div>
          {
            enabled ?
              <SpinnerButton
                isSpinning={isSearching}
                onPress={this.onSearchPress}
              >
                {'Search Soulseek'}
              </SpinnerButton> :
              null
          }
        </div>

        <div className={styles.help}>
          {'Peer folders through your slskd, judged by the same rules as indexer results. A grab downloads the whole folder and imports it for this book.'}
        </div>

        {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}

        {this.renderGrabs()}
        {this.renderResults()}
      </div>
    );
  }
}

BookSoulseek.propTypes = {
  bookId: PropTypes.number.isRequired
};

export default BookSoulseek;
