import { push } from 'connected-react-router';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import IconButton from 'Components/Link/IconButton';
import Link from 'Components/Link/Link';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Menu from 'Components/Menu/Menu';
import MenuContent from 'Components/Menu/MenuContent';
import MenuItem from 'Components/Menu/MenuItem';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { align, icons, kinds, sizes } from 'Helpers/Props';
import { fetchBooks } from 'Store/Actions/bookActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import RecordingCandidatesModal from './RecordingCandidatesModal';
import {
  abridgedLabel,
  describeRecording,
  formatAbridgedSource,
  formatFiles,
  formatNarrators,
  formatRuntime,
  formatYear
} from './recordingFormat';
import RemoveRecordingModal from './RemoveRecordingModal';
import styles from './BookRecordings.css';

const columns = [
  { name: 'narrators', label: 'Narrator', isVisible: true },
  { name: 'abridged', label: 'Abridged', isVisible: true },
  { name: 'runtime', label: 'Runtime', isVisible: true },
  { name: 'marketplace', label: 'Marketplace', isVisible: true },
  { name: 'edition', label: 'Edition', isVisible: true },
  { name: 'files', label: 'Files', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

function abridgedKind(abridged) {
  if (abridged === 'abridged') {
    return kinds.WARNING;
  }

  return abridged === 'unabridged' ? kinds.SUCCESS : kinds.DISABLED;
}

function filesKind(recording) {
  if (recording.hasFiles) {
    return kinds.SUCCESS;
  }

  return recording.monitored ? kinds.DANGER : kinds.DISABLED;
}

function normalizeName(name) {
  return (name || '').toLowerCase().replace(/[^a-z0-9]/g, '');
}

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}

// Talearr: the recordings of an audiobook (docs/TALEARR_RECORDINGS.md). Each recording is one of
// upstream's wanted or copy rows with its own edition and files; the API behind this section is
// /api/v1/talearr/recordings.
class BookRecordings extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._abortRequest = null;

    this.state = {
      isFetching: false,
      error: null,
      view: null,
      isRefreshing: false,
      notice: null,
      picker: null,
      addingKey: null,
      removing: null,
      isRemoving: false,
      removeError: null
    };
  }

  componentDidMount() {
    this.fetchRecordings();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.bookId !== this.props.bookId) {
      this.setState({ view: null, notice: null });
      this.fetchRecordings();
    }
  }

  componentWillUnmount() {
    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  //
  // Data

  fetchRecordings = () => {
    if (this._abortRequest) {
      this._abortRequest();
    }

    this.setState({ isFetching: true });

    const { request, abortRequest } = createAjaxRequest({
      url: '/talearr/recordings',
      data: { bookId: this.props.bookId }
    });

    this._abortRequest = abortRequest;

    request.done((data) => {
      this.setState({ isFetching: false, error: null, view: data });
    });

    request.fail((xhr) => {
      if (xhr.aborted) {
        return;
      }

      this.setState({
        isFetching: false,
        view: null,
        error: xhr.status === 404 ? null : readError(xhr, 'Unable to load the recordings of this book')
      });
    });
  };

  // The author's book list picks the new or removed copy row up on its next refresh; refetching it
  // here would reload this page and drop the notice.
  libraryChanged = () => {
    this.fetchRecordings();
  };

  addRecording = (body, key) => {
    this.setState({ addingKey: key, notice: null });

    const { request } = createAjaxRequest({
      url: '/talearr/recordings',
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({ bookId: this.props.bookId, requestedBy: 'ui', ...body })
    });

    request.done((data) => {
      const recording = data.recording;
      const what = recording ? describeRecording(recording) : 'the recording';
      const text = data.action === 'existing' ?
        `You already have ${what}.` :
        `Added ${what}${data.searchQueued ? ', searching now' : ''}.`;

      this.setState({
        addingKey: null,
        picker: null,
        notice: { kind: data.action === 'existing' ? kinds.INFO : kinds.SUCCESS, text }
      });

      this.libraryChanged();
    });

    request.fail((xhr) => {
      const problem = xhr.responseJSON || {};

      if (xhr.status === 409) {
        this.setState({
          addingKey: null,
          picker: {
            mode: 'edition',
            title: 'Pick an edition',
            message: problem.message || 'Several editions fit equally well; pick one.',
            candidates: problem.candidates || []
          }
        });

        return;
      }

      this.setState({
        addingKey: null,
        notice: { kind: kinds.WARNING, text: readError(xhr, 'Unable to add the recording') }
      });
    });
  };

  //
  // Listeners

  onRefreshPress = () => {
    this.setState({ isRefreshing: true });

    const { request } = createAjaxRequest({
      url: `/talearr/recordings/refresh?bookId=${this.props.bookId}`,
      method: 'POST',
      dataType: 'json'
    });

    request.always(() => {
      this.setState({ isRefreshing: false });
    });

    request.done(() => {
      this.setState({ notice: { kind: kinds.INFO, text: 'Refreshing Audible facts in the background. Reload this page in a moment.' } });
    });
  };

  onAddKindPress = (kind) => {
    this.addRecording({ kind }, -1);
  };

  onAnotherNarratorPress = () => {
    this.setState({
      picker: {
        mode: 'narrator',
        title: 'Another narrator',
        message: 'Talearr picks the best edition read by the narrator you choose.',
        candidates: this.getOtherNarratorCandidates()
      }
    });
  };

  onPickEditionPress = () => {
    this.setState({
      picker: {
        mode: 'edition',
        title: 'Pick an edition',
        message: null,
        candidates: this.state.view.candidates || []
      }
    });
  };

  onCandidateAddPress = (candidate) => {
    const { picker } = this.state;

    if (picker.mode === 'narrator' && candidate.narrators && candidate.narrators.length) {
      this.addRecording({ kind: 'narrator', narrator: candidate.narrators[0] }, candidate.editionId);
    } else {
      this.addRecording({ kind: 'edition', editionId: candidate.editionId }, candidate.editionId);
    }
  };

  onPickerClose = () => {
    this.setState({ picker: null });
  };

  onRemoveOpen = (recording) => {
    this.setState({ removing: recording, removeError: null });
  };

  onRemoveClose = () => {
    this.setState({ removing: null, removeError: null });
  };

  onRemovePress = (deleteFiles) => {
    const { removing, view } = this.state;
    const primary = (view.recordings || []).find((r) => r.role === 'primary');

    this.setState({ isRemoving: true, removeError: null });

    const { request } = createAjaxRequest({
      url: `/talearr/recordings/${removing.bookId}?deleteFiles=${deleteFiles}`,
      method: 'DELETE',
      dataType: 'json'
    });

    request.done(() => {
      this.setState({
        isRemoving: false,
        removing: null,
        notice: { kind: kinds.SUCCESS, text: `Removed ${describeRecording(removing)}.` }
      });

      if (removing.bookId === this.props.bookId && primary) {
        this.props.dispatchFetchBooks({ authorId: this.props.authorId });
        this.props.dispatchPush(`${window.Chaptarr.urlBase}/book/${primary.bookId}`);
        return;
      }

      this.libraryChanged();
    });

    request.fail((xhr) => {
      this.setState({ isRemoving: false, removeError: readError(xhr, 'Unable to remove the recording') });
    });
  };

  //
  // Helpers

  getOtherNarratorCandidates() {
    const { view } = this.state;
    const taken = new Set();

    (view.recordings || []).forEach((r) => (r.narrators || []).forEach((n) => taken.add(normalizeName(n))));

    const seen = new Set();

    return (view.candidates || []).filter((c) => {
      const names = (c.narrators || []).map(normalizeName).filter(Boolean);
      const key = names.join('|');

      if (!names.length || names.some((n) => taken.has(n)) || seen.has(key)) {
        return false;
      }

      seen.add(key);
      return true;
    });
  }

  getAddOption(kind) {
    const { view } = this.state;
    const recordings = view.recordings || [];
    const candidates = view.candidates || [];
    const label = kind === 'unabridged' ? 'unabridged' : 'abridged';

    if (recordings.some((r) => r.abridged === kind)) {
      return { isDisabled: true, reason: `You already have the ${label} recording` };
    }

    if (!candidates.some((c) => c.abridged === kind)) {
      return { isDisabled: true, reason: `No ${label} edition is known for this book` };
    }

    return { isDisabled: false, reason: null };
  }

  //
  // Render

  renderAddMenu() {
    const { view, addingKey } = this.state;
    const unabridged = this.getAddOption('unabridged');
    const abridged = this.getAddOption('abridged');
    const otherNarrators = this.getOtherNarratorCandidates();
    const hasCandidates = (view.candidates || []).length > 0;

    return (
      <Menu alignMenu={align.RIGHT}>
        <Button
          size={sizes.SMALL}
          kind={kinds.PRIMARY}
          isDisabled={addingKey != null}
        >
          {addingKey === -1 ? 'Adding...' : 'Add another recording'}
        </Button>

        <MenuContent>
          <MenuItem
            isDisabled={unabridged.isDisabled}
            title={unabridged.reason || 'Add the unabridged recording as its own copy and search for it'}
            onPress={() => this.onAddKindPress('unabridged')}
          >
            {'Unabridged version'}
            {unabridged.reason && <span className={styles.menuReason}>{unabridged.reason}</span>}
          </MenuItem>

          <MenuItem
            isDisabled={abridged.isDisabled}
            title={abridged.reason || 'Add the abridged recording as its own copy and search for it'}
            onPress={() => this.onAddKindPress('abridged')}
          >
            {'Abridged version'}
            {abridged.reason && <span className={styles.menuReason}>{abridged.reason}</span>}
          </MenuItem>

          <MenuItem
            isDisabled={!otherNarrators.length}
            onPress={this.onAnotherNarratorPress}
          >
            {'Another narrator...'}
            {!otherNarrators.length && <span className={styles.menuReason}>{'No other narrator is known'}</span>}
          </MenuItem>

          <MenuItem
            isDisabled={!hasCandidates}
            onPress={this.onPickEditionPress}
          >
            {'Pick an edition...'}
            {!hasCandidates && <span className={styles.menuReason}>{'No other audiobook edition is known'}</span>}
          </MenuItem>
        </MenuContent>
      </Menu>
    );
  }

  renderPrompt() {
    const { view, addingKey } = this.state;
    const recordings = view.recordings || [];
    const primary = recordings.find((r) => r.role === 'primary');

    if (
      !primary ||
      primary.abridged !== 'abridged' ||
      recordings.some((r) => r.abridged === 'unabridged') ||
      !(view.candidates || []).some((c) => c.abridged === 'unabridged')
    ) {
      return null;
    }

    return (
      <Alert kind={kinds.INFO} className={styles.prompt}>
        <span>{'You have the abridged recording. Add the unabridged one? Both are kept.'}</span>
        <Button
          className={styles.promptButton}
          size={sizes.SMALL}
          kind={kinds.PRIMARY}
          isDisabled={addingKey != null}
          onPress={() => this.addRecording({ kind: 'unabridged', search: true }, -1)}
        >
          {'Add and search'}
        </Button>
      </Alert>
    );
  }

  renderRow(recording, marketplace) {
    const isCurrent = recording.bookId === this.props.bookId;
    const year = formatYear(recording.releaseDate);

    return (
      <TableRow
        key={recording.bookId}
        className={isCurrent ? styles.currentRow : undefined}
      >
        <TableRowCell>
          {
            isCurrent ?
              <span>{formatNarrators(recording.narrators)}</span> :
              <Link to={`/book/${recording.bookId}`}>
                {formatNarrators(recording.narrators)}
              </Link>
          }
          {
            recording.role === 'primary' &&
              <span className={styles.roleLabel}>
                {'primary'}
              </span>
          }
        </TableRowCell>

        <TableRowCell>
          <Label
            kind={abridgedKind(recording.abridged)}
            title={recording.abridgedReason || ''}
          >
            {abridgedLabel(recording.abridged)}
          </Label>
          <span
            className={styles.source}
            title={recording.abridgedReason || ''}
          >
            {recording.abridgedSource === 'none' ? 'no source yet' : `from ${formatAbridgedSource(recording.abridgedSource)}`}
          </span>
        </TableRowCell>

        <TableRowCell>
          {formatRuntime(recording.runtimeMinutes)}
        </TableRowCell>

        <TableRowCell>
          {(marketplace || '').toUpperCase()}
          {
            recording.asin &&
              <Link
                className={styles.asin}
                to={recording.audibleUrl}
                title="Open on Audible"
              >
                {recording.asin}
              </Link>
          }
        </TableRowCell>

        <TableRowCell>
          {[recording.publisher, year].filter(Boolean).join(' ') || '-'}
        </TableRowCell>

        <TableRowCell>
          <Label kind={filesKind(recording)}>
            {recording.hasFiles ? 'Downloaded' : formatFiles(recording)}
          </Label>
          {
            recording.hasFiles &&
              <span className={styles.source} title={recording.path || ''}>
                {formatFiles(recording)}
              </span>
          }
        </TableRowCell>

        <TableRowCell className={styles.actions}>
          <IconButton
            name={icons.DELETE}
            title={recording.role === 'primary' ? 'The primary recording is the book itself' : 'Remove this recording'}
            onPress={() => this.onRemoveOpen(recording)}
          />
        </TableRowCell>
      </TableRow>
    );
  }

  render() {
    const {
      isFetching,
      error,
      view,
      isRefreshing,
      notice,
      picker,
      addingKey,
      removing,
      isRemoving,
      removeError
    } = this.state;

    if (!view) {
      if (isFetching) {
        return <LoadingIndicator />;
      }

      return error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null;
    }

    const recordings = view.recordings || [];

    return (
      <div className={styles.container}>
        <div className={styles.header}>
          <div className={styles.title}>
            {'Recordings'}
            <span className={styles.count}>{recordings.length}</span>
          </div>

          <div className={styles.headerActions}>
            <span className={styles.marketplace}>
              {`Marketplace: ${(view.marketplace || '').toUpperCase()}`}
            </span>

            <SpinnerButton
              size={sizes.SMALL}
              isSpinning={isRefreshing}
              title="Look the recordings up on Audible again, ignoring the cache"
              onPress={this.onRefreshPress}
            >
              {'Refresh'}
            </SpinnerButton>

            {this.renderAddMenu()}
          </div>
        </div>

        {this.renderPrompt()}

        {
          notice &&
            <Alert kind={notice.kind} className={styles.notice}>
              {notice.text}
            </Alert>
        }

        <Table columns={columns}>
          <TableBody>
            {recordings.map((recording) => this.renderRow(recording, view.marketplace))}
          </TableBody>
        </Table>

        <RecordingCandidatesModal
          isOpen={!!picker}
          title={picker ? picker.title : ''}
          message={picker ? picker.message : null}
          candidates={picker ? picker.candidates : []}
          addingKey={addingKey}
          onAddPress={this.onCandidateAddPress}
          onModalClose={this.onPickerClose}
        />

        <RemoveRecordingModal
          isOpen={!!removing}
          recording={removing}
          isRemoving={isRemoving}
          error={removeError}
          onRemovePress={this.onRemovePress}
          onModalClose={this.onRemoveClose}
        />
      </div>
    );
  }
}

BookRecordings.propTypes = {
  bookId: PropTypes.number.isRequired,
  authorId: PropTypes.number.isRequired,
  dispatchFetchBooks: PropTypes.func.isRequired,
  dispatchPush: PropTypes.func.isRequired
};

const mapDispatchToProps = {
  dispatchFetchBooks: fetchBooks,
  dispatchPush: push
};

export default connect(null, mapDispatchToProps)(BookRecordings);
