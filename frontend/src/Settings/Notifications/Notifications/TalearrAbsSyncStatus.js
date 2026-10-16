import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import SpinnerButton from 'Components/Link/SpinnerButton';
import { kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './TalearrAbsSyncStatus.css';

function formatTime(value) {
  if (!value) {
    return 'never';
  }

  const date = new Date(value);

  return Number.isNaN(date.getTime()) ? 'never' : date.toLocaleString();
}

// TaleArr: library sync status of a saved Audiobookshelf connection, with "Sync now"
// (docs/TALEARR_AUDIOBOOKSHELF.md). Shown under the library mappings.
class TalearrAbsSyncStatus extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      status: null,
      isSyncing: false,
      message: null
    };
  }

  componentDidMount() {
    this.fetchStatus();
  }

  componentWillUnmount() {
    clearTimeout(this._timeout);
  }

  //
  // Control

  fetchStatus = () => {
    const { request } = createAjaxRequest({
      url: '/talearr/audiobookshelf/status'
    });

    request.done((data) => {
      const status = (data || []).find((connection) => connection.id === this.props.connectionId) || null;
      this.setState({ status });
    });
  };

  onSyncPress = () => {
    this.setState({ isSyncing: true, message: null });

    const { request } = createAjaxRequest({
      url: `/talearr/audiobookshelf/sync?connectionId=${this.props.connectionId}`,
      method: 'POST'
    });

    request.done(() => {
      this.setState({ message: 'Sync started. It runs in the background; this refreshes in a few seconds.' });
      this._timeout = setTimeout(() => {
        this.setState({ isSyncing: false });
        this.fetchStatus();
      }, 5000);
    });

    request.fail(() => {
      this.setState({ isSyncing: false, message: 'Could not start the sync.' });
    });
  };

  //
  // Render

  render() {
    const {
      status,
      isSyncing,
      message
    } = this.state;

    if (!status) {
      return null;
    }

    if (!status.syncLibrary) {
      return (
        <Alert kind={kinds.INFO}>
          {'TaleArr does not read this Audiobookshelf library (Sync Library is off, or the connection is incomplete).'}
        </Alert>
      );
    }

    return (
      <div className={styles.container}>
        <div className={styles.text}>
          <div className={styles.title}>
            {'TaleArr library sync'}
          </div>

          <div>
            {`Last read: ${formatTime(status.lastSyncAt)}. ${status.itemCount} items, ${status.matchedCount} matched to books in TaleArr.`}
            {status.skipOwned ? ' Books already there are not grabbed.' : ' Grabs are not affected.'}
          </div>

          {
            !!status.lastError &&
              <div className={styles.error}>
                {`Last sync failed: ${status.lastError}`}
              </div>
          }

          {
            !!message &&
              <div className={styles.message}>
                {message}
              </div>
          }
        </div>

        <SpinnerButton
          isSpinning={isSyncing}
          onPress={this.onSyncPress}
        >
          {'Sync now'}
        </SpinnerButton>
      </div>
    );
  }
}

TalearrAbsSyncStatus.propTypes = {
  connectionId: PropTypes.number.isRequired
};

export default TalearrAbsSyncStatus;
