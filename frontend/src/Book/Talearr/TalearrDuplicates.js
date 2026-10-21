import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
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
import { icons, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './TalearrDuplicates.css';

const authorColumns = [
  { name: 'keep', label: 'Keep', isVisible: true },
  { name: 'name', label: 'Author', isVisible: true },
  { name: 'path', label: 'Folder', isVisible: true },
  { name: 'books', label: 'Books', isVisible: true },
  { name: 'files', label: 'Files', isVisible: true },
  { name: 'ids', label: 'Provider ids', isVisible: true }
];

const bookColumns = [
  { name: 'keep', label: 'Keep', isVisible: true },
  { name: 'title', label: 'Row', isVisible: true },
  { name: 'monitored', label: 'Monitored', isVisible: true },
  { name: 'editions', label: 'Editions', isVisible: true },
  { name: 'files', label: 'Files', isVisible: true },
  { name: 'ids', label: 'Work ids', isVisible: true }
];

const changeLabels = {
  books: 'books moved',
  booksRemoved: 'duplicate book rows removed',
  authorsRemoved: 'duplicate author rows removed',
  editions: 'editions moved',
  editionsFolded: 'identical editions folded together',
  bookFiles: 'book files re-linked (files on disk untouched)',
  history: 'history entries moved',
  downloadHistory: 'download history entries moved',
  pendingReleases: 'pending releases moved',
  pendingImports: 'pending imports moved',
  metadataFiles: 'metadata file records moved',
  extraFiles: 'extra file records moved',
  otherFiles: 'other file records moved',
  legacyBlacklist: 'legacy blacklist rows moved',
  seriesLinks: 'series links moved',
  series: 'author series links moved',
  narratorLinks: 'narrator links moved',
  blocklist: 'blocklist entries extended'
};

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}

function setKey(kind, set) {
  return `${kind}:${set.key}`;
}

// Talearr: possible duplicate authors and book rows, with a merge that keeps one row and moves
// everything else onto it. Files on disk are never touched.
class TalearrDuplicates extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._abortRequest = null;

    this.state = {
      isFetching: true,
      error: null,
      authors: [],
      books: [],
      keep: {},
      pending: null,
      preview: null,
      previewError: null,
      canForce: false,
      isMerging: false,
      lastResult: null,
      mergeAll: null,
      mergeAllError: null,
      isMergingAll: false,
      lastMergeAll: null
    };
  }

  componentDidMount() {
    this.fetchCandidates();
  }

  componentWillUnmount() {
    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  //
  // Data

  fetchCandidates = () => {
    if (this._abortRequest) {
      this._abortRequest();
    }

    this.setState({ isFetching: true });

    const { request, abortRequest } = createAjaxRequest({
      url: '/talearr/merge/candidates'
    });

    this._abortRequest = abortRequest;

    request.done((data) => {
      this.setState({
        isFetching: false,
        error: null,
        authors: data.authors || [],
        books: data.books || [],
        keep: {}
      });
    });

    request.fail((xhr) => {
      if (!xhr.aborted) {
        this.setState({ isFetching: false, error: readError(xhr, 'Unable to load possible duplicates.') });
      }
    });
  };

  postMerge(pending, dryRun, force) {
    return createAjaxRequest({
      url: `/talearr/merge/${pending.kind}`,
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        targetId: pending.targetId,
        sourceIds: pending.sourceIds,
        dryRun,
        force
      })
    }).request;
  }

  getTargetId(kind, set, items) {
    const chosen = this.state.keep[setKey(kind, set)];
    return chosen || items[0].id;
  }

  //
  // Listeners

  onRefreshPress = () => {
    this.fetchCandidates();
  };

  onKeepPress = (kind, set, id) => {
    this.setState({ keep: { ...this.state.keep, [setKey(kind, set)]: id } });
  };

  onMergePress = (kind, set, items, title) => {
    const targetId = this.getTargetId(kind, set, items);
    const pending = {
      kind,
      title,
      targetId,
      sourceIds: items.map((i) => i.id).filter((id) => id !== targetId),
      force: false
    };

    this.setState({ pending, preview: null, previewError: null, canForce: false, isMerging: true });

    this.postMerge(pending, true, false)
      .done((preview) => {
        this.setState({ preview, isMerging: false });
      })
      .fail((xhr) => {
        const message = readError(xhr, 'Unable to preview the merge.');
        this.setState({ previewError: message, canForce: message.indexOf('force=true') >= 0, isMerging: false });
      });
  };

  onConfirmMerge = () => {
    const { pending, preview, canForce } = this.state;

    if (!pending) {
      return;
    }

    if (!preview && canForce && !pending.force) {
      const forced = { ...pending, force: true };
      this.setState({ pending: forced, isMerging: true, previewError: null, canForce: false });

      this.postMerge(forced, true, true)
        .done((result) => this.setState({ preview: result, isMerging: false }))
        .fail((xhr) => this.setState({ previewError: readError(xhr, 'Unable to preview the merge.'), isMerging: false }));

      return;
    }

    if (!preview) {
      this.onCancelMerge();
      return;
    }

    this.setState({ isMerging: true });

    this.postMerge(pending, false, pending.force)
      .done((result) => {
        this.setState({ pending: null, preview: null, isMerging: false, lastResult: result });
        this.fetchCandidates();
      })
      .fail((xhr) => {
        this.setState({ previewError: readError(xhr, 'The merge failed; nothing was changed.'), preview: null, isMerging: false });
      });
  };

  postMergeAll(dryRun) {
    return createAjaxRequest({
      url: '/talearr/merge/books/all',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ dryRun })
    }).request;
  }

  // Talearr: one preview and one confirm for every book set the page lists.
  onMergeAllPress = () => {
    this.setState({ mergeAll: { merged: [], skipped: [] }, mergeAllError: null, isMergingAll: true });

    this.postMergeAll(true)
      .done((preview) => this.setState({ mergeAll: preview, isMergingAll: false }))
      .fail((xhr) => this.setState({ mergeAllError: readError(xhr, 'Unable to preview the merge.'), isMergingAll: false }));
  };

  onConfirmMergeAll = () => {
    const { mergeAll } = this.state;

    if (!mergeAll || !mergeAll.merged.length) {
      this.onCancelMergeAll();
      return;
    }

    this.setState({ isMergingAll: true });

    this.postMergeAll(false)
      .done((result) => {
        this.setState({ mergeAll: null, isMergingAll: false, lastMergeAll: result });
        this.fetchCandidates();
      })
      .fail((xhr) => this.setState({ mergeAllError: readError(xhr, 'The merge failed; nothing was changed.'), isMergingAll: false }));
  };

  onCancelMergeAll = () => {
    this.setState({ mergeAll: null, mergeAllError: null, isMergingAll: false });
  };

  onCancelMerge = () => {
    this.setState({ pending: null, preview: null, previewError: null, canForce: false, isMerging: false });
  };

  //
  // Render

  renderKeep(kind, set, items, id) {
    const isKept = this.getTargetId(kind, set, items) === id;

    return (
      <TableRowCell>
        {
          isKept ?
            <Label kind={kinds.SUCCESS}>{'Keep'}</Label> :
            <Button
              size={sizes.SMALL}
              onPress={() => this.onKeepPress(kind, set, id)}
            >
              {'Keep this'}
            </Button>
        }
      </TableRowCell>
    );
  }

  renderMessage() {
    const { pending, preview, previewError, isMerging } = this.state;

    if (!pending) {
      return '';
    }

    if (previewError) {
      return <Alert kind={kinds.DANGER}>{previewError}</Alert>;
    }

    if (!preview) {
      return isMerging ? <LoadingIndicator /> : '';
    }

    const changes = Object.keys(preview.changes || {});

    return (
      <div>
        <div className={styles.summary}>
          {`Keep #${preview.targetId} "${preview.targetName}" and merge #${pending.sourceIds.join(', #')} into it. This moves:`}
        </div>

        <ul className={styles.changes}>
          {
            changes.length ?
              changes.map((key) => (
                <li key={key}>{`${preview.changes[key]} ${changeLabels[key] || key}`}</li>
              )) :
              <li>{'Nothing else references the merged rows.'}</li>
          }
        </ul>

        {
          (preview.notes || []).map((note) => (
            <div key={note} className={styles.note}>{note}</div>
          ))
        }

        <div className={styles.note}>
          {'Files on disk are not moved, renamed or deleted. The merged-away rows are removed from the database.'}
        </div>
      </div>
    );
  }

  renderMergeAllMessage() {
    const { mergeAll, mergeAllError, isMergingAll } = this.state;

    if (mergeAllError) {
      return <Alert kind={kinds.DANGER}>{mergeAllError}</Alert>;
    }

    if (!mergeAll || (isMergingAll && !mergeAll.merged.length && !mergeAll.skipped.length)) {
      return <LoadingIndicator />;
    }

    return (
      <div>
        <div className={styles.summary}>
          {mergeAll.merged.length ?
            `Merge ${mergeAll.merged.length} duplicate book ${mergeAll.merged.length === 1 ? 'set' : 'sets'}, keeping the row with files (or the monitored one) in each:` :
            'There is nothing to merge automatically.'}
        </div>

        <ul className={styles.changes}>
          {
            mergeAll.merged.map((item) => (
              <li key={item.targetId}>{`"${item.targetName}" (#${item.targetId}) takes #${item.mergedIds.join(', #')}`}</li>
            ))
          }
        </ul>

        {
          mergeAll.skipped.length ?
            <div className={styles.note}>
              {`Left for you to review: ${mergeAll.skipped.map((s) => `"${s.title}" (${s.reason})`).join(', ')}.`}
            </div> :
            null
        }

        <div className={styles.note}>
          {'Files on disk are not moved, renamed or deleted. The merged-away rows are removed from the database.'}
        </div>
      </div>
    );
  }

  renderAuthors() {
    const { authors } = this.state;

    if (!authors.length) {
      return <Alert kind={kinds.INFO}>{'No possible duplicate authors.'}</Alert>;
    }

    return authors.map((set) => (
      <div key={set.key} className={styles.set}>
        <div className={styles.setHeader}>
          <span className={styles.setTitle}>{set.authors[0].name}</span>
          <Button
            kind={kinds.PRIMARY}
            size={sizes.SMALL}
            onPress={() => this.onMergePress('authors', set, set.authors, set.authors[0].name)}
          >
            {'Merge'}
          </Button>
        </div>

        <Table columns={authorColumns}>
          <TableBody>
            {
              set.authors.map((author) => (
                <TableRow key={author.id}>
                  {this.renderKeep('authors', set, set.authors, author.id)}
                  <TableRowCell>
                    <Link to={`/author/${author.id}`}>{author.name}</Link>
                    <span className={styles.rowId}>{`#${author.id}`}</span>
                  </TableRowCell>
                  <TableRowCell>{author.path}</TableRowCell>
                  <TableRowCell>{author.bookCount}</TableRowCell>
                  <TableRowCell>{author.fileCount}</TableRowCell>
                  <TableRowCell>{author.providerIds.join(', ')}</TableRowCell>
                </TableRow>
              ))
            }
          </TableBody>
        </Table>
      </div>
    ));
  }

  renderBooks() {
    const { books } = this.state;

    if (!books.length) {
      return <Alert kind={kinds.INFO}>{'No possible duplicate book rows.'}</Alert>;
    }

    return books.map((set) => (
      <div key={set.key} className={styles.set}>
        <div className={styles.setHeader}>
          <span className={styles.setTitle}>{set.title}</span>
          <span className={styles.setMeta}>{`${set.authorName || 'Unknown author'}, ${set.mediaType}`}</span>
          <Button
            kind={kinds.PRIMARY}
            size={sizes.SMALL}
            onPress={() => this.onMergePress('books', set, set.books, set.title)}
          >
            {'Merge'}
          </Button>
        </div>

        <Table columns={bookColumns}>
          <TableBody>
            {
              set.books.map((book) => (
                <TableRow key={book.id}>
                  {this.renderKeep('books', set, set.books, book.id)}
                  <TableRowCell>
                    {book.title}
                    <span className={styles.rowId}>{`#${book.id}`}</span>
                    {
                      book.recordingCopy ?
                        <Label kind={kinds.WARNING}>{'Recording copy'}</Label> :
                        null
                    }
                  </TableRowCell>
                  <TableRowCell>{book.monitored ? 'Yes' : 'No'}</TableRowCell>
                  <TableRowCell>{book.editionCount}</TableRowCell>
                  <TableRowCell>{book.fileCount}</TableRowCell>
                  <TableRowCell>{book.workIds.join(', ')}</TableRowCell>
                </TableRow>
              ))
            }
          </TableBody>
        </Table>
      </div>
    ));
  }

  render() {
    const { isFetching, error, authors, books, pending, preview, canForce, isMerging, lastResult, mergeAll, isMergingAll, lastMergeAll } = this.state;
    const hasData = authors.length || books.length;

    let confirmLabel = 'Close';
    if (preview) {
      confirmLabel = 'Merge';
    } else if (canForce) {
      confirmLabel = 'Preview anyway';
    }

    return (
      <PageContent title="Duplicates">
        <PageToolbar>
          <PageToolbarSection>
            {/* Talearr: opened from the Books toolbar, so offer the way back. */}
            <PageToolbarButton
              label="Books"
              iconName={icons.BOOK}
              to="/books"
            />

            <PageToolbarButton
              label="Refresh"
              iconName={icons.REFRESH}
              isSpinning={isFetching}
              onPress={this.onRefreshPress}
            />

            <PageToolbarButton
              label="Merge All Books"
              iconName={icons.ORGANIZE}
              isDisabled={!books.length}
              onPress={this.onMergeAllPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <div className={styles.intro}>
            {'Possible duplicates: authors with the same name, and book rows of one author with the same title and media type. Ebook and audiobook rows of one book are siblings and are never listed or merged. Merge after reviewing the preview; files on disk are never touched.'}
          </div>

          {
            lastResult ?
              <Alert kind={kinds.SUCCESS}>
                {`Merged #${lastResult.mergedIds.join(', #')} into #${lastResult.targetId} "${lastResult.targetName}".`}
              </Alert> :
              null
          }

          {
            lastMergeAll ?
              <Alert kind={kinds.SUCCESS}>
                {`Merged ${lastMergeAll.merged.length} duplicate book ${lastMergeAll.merged.length === 1 ? 'set' : 'sets'}.`}
              </Alert> :
              null
          }

          {
            isFetching && !hasData ?
              <LoadingIndicator /> :
              null
          }

          {
            error ?
              <Alert kind={kinds.DANGER}>{error}</Alert> :
              null
          }

          {
            !isFetching && !error ?
              <div>
                <h2 className={styles.heading}>{'Authors'}</h2>
                {this.renderAuthors()}

                <h2 className={styles.heading}>{'Books'}</h2>
                {this.renderBooks()}
              </div> :
              null
          }
        </PageContentBody>

        <ConfirmModal
          isOpen={!!pending}
          kind={preview ? kinds.DANGER : kinds.PRIMARY}
          title={pending ? `Merge "${pending.title}"` : 'Merge'}
          message={this.renderMessage()}
          confirmLabel={confirmLabel}
          isSpinning={isMerging}
          onConfirm={this.onConfirmMerge}
          onCancel={this.onCancelMerge}
        />

        <ConfirmModal
          isOpen={!!mergeAll}
          kind={kinds.DANGER}
          title="Merge all duplicate books"
          message={this.renderMergeAllMessage()}
          confirmLabel={mergeAll && mergeAll.merged.length ? 'Merge All' : 'Close'}
          isSpinning={isMergingAll}
          onConfirm={this.onConfirmMergeAll}
          onCancel={this.onCancelMergeAll}
        />
      </PageContent>
    );
  }
}

export default TalearrDuplicates;
