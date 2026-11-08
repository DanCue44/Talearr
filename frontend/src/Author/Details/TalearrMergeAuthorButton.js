import { push } from 'connected-react-router';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { describeChange, readMergeError } from 'Book/Talearr/talearrMergeLabels';
import Alert from 'Components/Alert';
import TextInput from 'Components/Form/TextInput';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import Scroller from 'Components/Scroller/Scroller';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds, messageTypes, scrollDirections, sizes } from 'Helpers/Props';
import { showMessage } from 'Store/Actions/appActions';
import { fetchAuthor } from 'Store/Actions/authorActions';
import { removeItem } from 'Store/Actions/baseActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { repopulatePage } from 'Utilities/pagePopulator';
import styles from './TalearrMergeAuthorButton.css';

// Only this many authors are listed before the filter narrows them down.
const listLimit = 100;

const columns = [
  { name: 'keep', label: 'Keep', isVisible: true },
  { name: 'name', label: 'Author', isVisible: true },
  { name: 'books', label: 'Books', isVisible: true },
  { name: 'files', label: 'Files', isVisible: true }
];

// Previewed changes listed first, in this order; anything else the server reports follows.
const changeOrder = ['books', 'bookFilesWithBooks', 'series'];

// Last word of a name ("Wehr, Dietmar" and "Dietmar A. Wehr" both give "wehr"), used to list
// authors with the same last name first.
function lastName(name) {
  const value = (name || '').toLowerCase();
  const comma = value.indexOf(',');
  const ordered = comma > 0 && comma === value.lastIndexOf(',') ? `${value.substring(comma + 1)} ${value.substring(0, comma)}` : value;
  const words = ordered.normalize('NFD').replace(/[̀-ͯ]/g, '').split(/[^a-z0-9]+/).filter(Boolean);

  return words.length > 1 ? words[words.length - 1] : '';
}

function toItem(author) {
  const statistics = author.statistics || {};

  return {
    id: author.id,
    authorName: author.authorName,
    bookCount: statistics.totalBookCount || statistics.bookCount || 0,
    fileCount: statistics.bookFileCount || 0
  };
}

function orderedChanges(changes) {
  const keys = Object.keys(changes || {});
  const first = changeOrder.filter((key) => keys.includes(key));

  return [...first, ...keys.filter((key) => !first.includes(key))];
}

function createMapStateToProps() {
  return createSelector(
    (state) => state.authors.items,
    (state, { authorId }) => authorId,
    (authors, authorId) => {
      return {
        authors: authors.filter((author) => author.id !== authorId).map(toItem),
        current: toItem(authors.find((author) => author.id === authorId) || { id: authorId })
      };
    }
  );
}

const mapDispatchToProps = {
  push,
  fetchAuthor,
  removeItem,
  showMessage
};

const initialState = {
  isOpen: false,
  filter: '',
  otherId: null,
  keepId: null,
  preview: null,
  previewError: null,
  canForce: false,
  force: false,
  isPreviewing: false,
  isMerging: false
};

// Talearr: "Merge Author" on the author page. Pick the other author, choose which one to keep,
// review the dry run of POST /talearr/merge/authors, then merge and open the kept author.
class TalearrMergeAuthorButton extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = { ...initialState };
    this._abortPreview = null;
  }

  componentWillUnmount() {
    this.abortPreview();
  }

  //
  // Control

  abortPreview() {
    if (this._abortPreview) {
      this._abortPreview();
      this._abortPreview = null;
    }
  }

  getOther() {
    return this.props.authors.find((author) => author.id === this.state.otherId);
  }

  getPair() {
    const { current } = this.props;
    const { keepId } = this.state;
    const other = this.getOther();
    const target = keepId === other.id ? other : current;
    const source = target === current ? other : current;

    return { target, source };
  }

  postMerge(target, source, dryRun, force) {
    return createAjaxRequest({
      url: '/talearr/merge/authors',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        targetId: target.id,
        sourceIds: [source.id],
        dryRun,
        force
      })
    });
  }

  runPreview(force) {
    this.abortPreview();

    const { target, source } = this.getPair();
    const { request, abortRequest } = this.postMerge(target, source, true, force);

    this._abortPreview = abortRequest;
    this.setState({ preview: null, previewError: null, canForce: false, force, isPreviewing: true });

    request.done((preview) => {
      this._abortPreview = null;
      this.setState({ preview, isPreviewing: false });
    });

    request.fail((xhr) => {
      if (xhr.aborted) {
        return;
      }

      this._abortPreview = null;
      const message = readMergeError(xhr, 'Unable to preview the merge.');
      // The API's "Send force=true ..." sentence is for API callers; the button below is the UI's way.
      this.setState({
        previewError: message.replace(/\s*Send force=true[^.]*\./, ''),
        canForce: !force && message.indexOf('force=true') >= 0,
        isPreviewing: false
      });
    });
  }

  //
  // Listeners

  onPress = () => {
    this.setState({ ...initialState, isOpen: true });
  };

  onModalClose = () => {
    this.abortPreview();
    this.setState({ ...initialState });
  };

  onFilterChange = ({ value }) => {
    this.setState({ filter: value });
  };

  onAuthorSelect = (other) => {
    const { current } = this.props;

    // Keep the one with more files, then more books; on a tie, the author whose page this is.
    const otherIsBigger = other.fileCount > current.fileCount ||
      (other.fileCount === current.fileCount && other.bookCount > current.bookCount);

    this.setState({ otherId: other.id, keepId: otherIsBigger ? other.id : current.id }, () => this.runPreview(false));
  };

  onChooseAnotherPress = () => {
    this.abortPreview();
    this.setState({ otherId: null, keepId: null, preview: null, previewError: null, canForce: false, force: false, isPreviewing: false });
  };

  onKeepPress = (id) => {
    this.setState({ keepId: id }, () => this.runPreview(this.state.force));
  };

  onPreviewAnywayPress = () => {
    this.runPreview(true);
  };

  onMergePress = () => {
    const { authorId } = this.props;
    const { force } = this.state;
    const { target, source } = this.getPair();

    this.setState({ isMerging: true });

    const { request } = this.postMerge(target, source, false, force);

    request.done((result) => {
      this.setState({ ...initialState });

      this.props.showMessage({
        id: `talearr-merge-author-${target.id}`,
        name: 'TalearrMergeAuthor',
        message: `Merged "${source.authorName}" into "${result.targetName || target.authorName}". Files on disk were not touched.`,
        type: messageTypes.SUCCESS,
        hideAfter: 8
      });

      if (target.id !== authorId) {
        this.props.push(`${window.Chaptarr.urlBase}/author/${target.id}`);
      }

      this.props.fetchAuthor({ id: target.id });
      this.props.removeItem({ section: 'authors', id: source.id });

      if (target.id === authorId) {
        repopulatePage();
      }
    });

    request.fail((xhr) => {
      this.setState({
        isMerging: false,
        preview: null,
        previewError: readMergeError(xhr, 'The merge failed; nothing was changed.')
      });
    });
  };

  //
  // Render

  renderPicker() {
    const { current, authors } = this.props;
    const { filter } = this.state;
    const filterLower = filter.trim().toLowerCase();
    const currentLast = lastName(current.authorName);

    const matches = authors
      .filter((author) => !filterLower || (author.authorName || '').toLowerCase().includes(filterLower))
      .map((author) => ({ ...author, similar: !!currentLast && lastName(author.authorName) === currentLast }))
      .sort((a, b) => (b.similar - a.similar) || (a.authorName || '').localeCompare(b.authorName || ''));

    const shown = matches.slice(0, listLimit);

    return (
      <>
        <div className={styles.intro}>
          {`Pick the author that is the same person as "${current.authorName}". You choose which one to keep on the next step and see what moves before anything changes.`}
        </div>

        <TextInput
          className={styles.filterInput}
          placeholder="Filter authors"
          name="filter"
          value={filter}
          autoFocus={true}
          onChange={this.onFilterChange}
        />

        <Scroller className={styles.scroller} autoFocus={false}>
          {
            shown.map((author) => (
              <Link
                key={author.id}
                className={styles.author}
                component="div"
                onPress={() => this.onAuthorSelect(author)}
              >
                <span className={styles.authorName}>{author.authorName}</span>
                {
                  author.similar ?
                    <Label kind={kinds.INFO}>{'Same last name'}</Label> :
                    null
                }
                <span className={styles.meta}>{`${author.bookCount} books, ${author.fileCount} files`}</span>
              </Link>
            ))
          }

          {
            matches.length ?
              null :
              <Alert kind={kinds.INFO}>{'No other author matches.'}</Alert>
          }

          {
            matches.length > shown.length ?
              <div className={styles.more}>
                {`${matches.length - shown.length} more; type to narrow the list.`}
              </div> :
              null
          }
        </Scroller>
      </>
    );
  }

  renderPreview() {
    const { preview, previewError, canForce, isPreviewing, force } = this.state;

    if (isPreviewing) {
      return <LoadingIndicator />;
    }

    if (previewError) {
      return (
        <div>
          <Alert kind={kinds.DANGER}>{previewError}</Alert>
          {
            canForce ?
              <Button
                kind={kinds.WARNING}
                onPress={this.onPreviewAnywayPress}
              >
                {'They are the same author, preview anyway'}
              </Button> :
              null
          }
        </div>
      );
    }

    if (!preview) {
      return null;
    }

    const keys = orderedChanges(preview.changes).filter((key) => key !== 'authorsRemoved');

    return (
      <div>
        <div className={styles.summary}>
          {`Keep #${preview.targetId} "${preview.targetName}" and merge #${preview.mergedIds.join(', #')} into it. This moves:`}
        </div>

        <ul className={styles.changes}>
          {
            keys.length ?
              keys.map((key) => (
                <li key={key}>{describeChange(key, preview.changes[key])}</li>
              )) :
              <li>{'Nothing else references the merged author.'}</li>
          }
        </ul>

        {
          (preview.notes || []).map((note) => (
            <div key={note} className={styles.note}>{note}</div>
          ))
        }

        {
          force ?
            <div className={styles.note}>
              {'The names differ and no provider id is shared; you confirmed they are the same author.'}
            </div> :
            null
        }

        <div className={styles.note}>
          {'Files on disk are not moved, renamed or deleted. The merged-away author is removed from Talearr, and a refresh will not bring it back.'}
        </div>
      </div>
    );
  }

  renderPair() {
    const { current } = this.props;
    const { keepId } = this.state;
    const other = this.getOther();

    return (
      <>
        <Table columns={columns}>
          <TableBody>
            {
              [current, other].map((author) => (
                <TableRow key={author.id}>
                  <TableRowCell>
                    {
                      keepId === author.id ?
                        <Label kind={kinds.SUCCESS}>{'Keep'}</Label> :
                        <Button
                          size={sizes.SMALL}
                          onPress={() => this.onKeepPress(author.id)}
                        >
                          {'Keep this'}
                        </Button>
                    }
                  </TableRowCell>
                  <TableRowCell>
                    {author.authorName}
                    <span className={styles.rowId}>{`#${author.id}`}</span>
                  </TableRowCell>
                  <TableRowCell>{author.bookCount}</TableRowCell>
                  <TableRowCell>{author.fileCount}</TableRowCell>
                </TableRow>
              ))
            }
          </TableBody>
        </Table>

        {this.renderPreview()}
      </>
    );
  }

  render() {
    const { current } = this.props;
    const { isOpen, preview, isPreviewing, isMerging } = this.state;
    const other = isOpen && this.state.otherId != null ? this.getOther() : null;

    return (
      <>
        <PageToolbarButton
          label="Merge Author"
          title="Merge another author that is the same person into this one, or this one into it"
          iconName={icons.GROUP}
          onPress={this.onPress}
        />

        <Modal
          isOpen={isOpen}
          size={sizes.LARGE}
          onModalClose={this.onModalClose}
        >
          <ModalContent onModalClose={this.onModalClose}>
            <ModalHeader>
              {`Merge Author: ${current.authorName}`}
            </ModalHeader>

            <ModalBody
              className={styles.modalBody}
              scrollDirection={other ? scrollDirections.VERTICAL : scrollDirections.NONE}
            >
              {other ? this.renderPair() : this.renderPicker()}
            </ModalBody>

            <ModalFooter>
              {
                other ?
                  <Button
                    className={styles.leftButton}
                    isDisabled={isMerging}
                    onPress={this.onChooseAnotherPress}
                  >
                    {'Choose another author'}
                  </Button> :
                  null
              }

              <Button onPress={this.onModalClose}>
                {'Cancel'}
              </Button>

              {
                other ?
                  <Button
                    kind={kinds.DANGER}
                    isDisabled={!preview || isPreviewing || isMerging}
                    onPress={this.onMergePress}
                  >
                    {isMerging ? 'Merging...' : 'Merge'}
                  </Button> :
                  null
              }
            </ModalFooter>
          </ModalContent>
        </Modal>
      </>
    );
  }
}

TalearrMergeAuthorButton.propTypes = {
  authorId: PropTypes.number.isRequired,
  current: PropTypes.object.isRequired,
  authors: PropTypes.arrayOf(PropTypes.object).isRequired,
  push: PropTypes.func.isRequired,
  fetchAuthor: PropTypes.func.isRequired,
  removeItem: PropTypes.func.isRequired,
  showMessage: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrMergeAuthorButton);
