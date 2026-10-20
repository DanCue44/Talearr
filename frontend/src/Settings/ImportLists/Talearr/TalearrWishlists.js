import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
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
import EditWishlistModal from './EditWishlistModal';
import { describeFormats, describeSource, formatNames, readError } from './wishlistFormat';
import WishlistPreviewTable from './WishlistPreviewTable';
import styles from './TalearrWishlists.css';

const columns = [
  { name: 'name', label: 'Wishlist', isVisible: true },
  { name: 'formats', label: 'Adds', isVisible: true },
  { name: 'lastSync', label: 'Last sync', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

function formatDate(value) {
  if (!value) {
    return 'Never';
  }

  return new Date(value).toLocaleString();
}

// Talearr: wishlists on Settings > Import Lists (docs/TALEARR_WISHLISTS.md). Not an import list
// provider (stock Chaptarr deletes provider rows it does not know); stored as talearr.wishlist.*
// Config rows and served by /api/v1/talearr/wishlists.
class TalearrWishlists extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      error: null,
      items: [],
      editing: null,
      isEditOpen: false,
      deleting: null,
      syncingId: null,
      result: null,
      resultTitle: null
    };
  }

  componentDidMount() {
    this.fetchWishlists();
  }

  //
  // Data

  fetchWishlists = () => {
    const { request } = createAjaxRequest({ url: '/talearr/wishlists' });

    request.done((items) => {
      this.setState({ isFetching: false, error: null, items });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load wishlists.') });
    });
  };

  //
  // Listeners

  onAddPress = () => {
    this.setState({ editing: null, isEditOpen: true });
  };

  onEditPress = (wishlist) => {
    this.setState({ editing: wishlist, isEditOpen: true });
  };

  onEditClose = () => {
    this.setState({ isEditOpen: false });
  };

  onSaved = () => {
    this.setState({ isEditOpen: false });
    this.fetchWishlists();
  };

  onSyncPress = (wishlist) => {
    this.setState({ syncingId: wishlist.id });

    const { request } = createAjaxRequest({
      url: `/talearr/wishlists/${wishlist.id}/sync`,
      method: 'POST',
      dataType: 'json'
    });

    request.done((result) => {
      this.setState({
        syncingId: null,
        resultTitle: `${wishlist.name}: synced`,
        result: { format: wishlist.format, rowCount: result.rows, items: result.items }
      });
      this.fetchWishlists();
    });

    request.fail((xhr) => {
      this.setState({ syncingId: null, error: readError(xhr, 'Unable to sync the wishlist.') });
    });
  };

  onPreviewPress = (wishlist) => {
    this.setState({ syncingId: wishlist.id });

    const { request } = createAjaxRequest({ url: `/talearr/wishlists/${wishlist.id}/preview` });

    request.done((preview) => {
      this.setState({ syncingId: null, resultTitle: `${wishlist.name}: preview`, result: preview });
    });

    request.fail((xhr) => {
      this.setState({ syncingId: null, error: readError(xhr, 'Unable to read the wishlist.') });
    });
  };

  onResultClose = () => {
    this.setState({ result: null });
  };

  onDeletePress = (wishlist) => {
    this.setState({ deleting: wishlist });
  };

  onDeleteCancel = () => {
    this.setState({ deleting: null });
  };

  onDeleteConfirm = () => {
    const { deleting } = this.state;
    const { request } = createAjaxRequest({ url: `/talearr/wishlists/${deleting.id}`, method: 'DELETE' });

    request.always(() => {
      this.setState({ deleting: null });
      this.fetchWishlists();
    });
  };

  //
  // Render

  render() {
    const { isFetching, error, items, editing, isEditOpen, deleting, syncingId, result, resultTitle } = this.state;

    return (
      <FieldSet legend="Wishlists">
        <div className={styles.intro}>
          {'Add the books on a wishlist: a Goodreads or StoryGraph export, an Audible wishlist (exported with audible-cli), any CSV with titles, or a Hardcover list. Only each listed book is monitored, never the rest of its author, and each row is added once. Use Preview to see the matches first.'}
        </div>

        {isFetching ? <LoadingIndicator /> : null}
        {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}

        {
          items.length ?
            <Table columns={columns}>
              <TableBody>
                {
                  items.map((wishlist) => {
                    return (
                      <TableRow key={wishlist.id}>
                        <TableRowCell>
                          <div className={styles.left}>
                            {wishlist.name}
                            {wishlist.enabled ? null : <Label kind={kinds.DISABLED}>{'Manual'}</Label>}
                            <span className={styles.sub}>
                              {[formatNames[wishlist.format], describeSource(wishlist)].filter(Boolean).join(': ')}
                            </span>
                          </div>
                        </TableRowCell>

                        <TableRowCell>{describeFormats(wishlist)}</TableRowCell>

                        <TableRowCell>
                          <div className={styles.left}>
                            {formatDate(wishlist.lastSyncAt)}
                            {
                              wishlist.lastError ?
                                <span className={styles.sub}><Label kind={kinds.DANGER}>{wishlist.lastError}</Label></span> :
                                <span className={styles.sub}>{wishlist.lastResult || ''}</span>
                            }
                          </div>
                        </TableRowCell>

                        <TableRowCell>
                          <div className={styles.rowActions}>
                            <SpinnerButton
                              size={sizes.SMALL}
                              isSpinning={syncingId === wishlist.id}
                              onPress={() => this.onSyncPress(wishlist)}
                            >
                              {'Sync now'}
                            </SpinnerButton>

                            <Button
                              size={sizes.SMALL}
                              onPress={() => this.onPreviewPress(wishlist)}
                            >
                              {'Preview'}
                            </Button>

                            <Button
                              size={sizes.SMALL}
                              onPress={() => this.onEditPress(wishlist)}
                            >
                              {'Edit'}
                            </Button>

                            <Button
                              size={sizes.SMALL}
                              kind={kinds.DANGER}
                              onPress={() => this.onDeletePress(wishlist)}
                            >
                              {'Remove'}
                            </Button>
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

        <div className={styles.addRow}>
          <Button
            kind={kinds.PRIMARY}
            onPress={this.onAddPress}
          >
            {'Add wishlist'}
          </Button>
        </div>

        <EditWishlistModal
          isOpen={isEditOpen}
          wishlist={editing}
          onSaved={this.onSaved}
          onModalClose={this.onEditClose}
        />

        <ConfirmModal
          isOpen={!!deleting}
          kind={kinds.DANGER}
          title="Remove wishlist"
          message={deleting ? `Remove "${deleting.name}"? The books it added stay in the library as they are.` : ''}
          confirmLabel="Remove"
          onConfirm={this.onDeleteConfirm}
          onCancel={this.onDeleteCancel}
        />

        <Modal
          isOpen={!!result}
          size={sizes.EXTRA_LARGE}
          onModalClose={this.onResultClose}
        >
          <ModalContent onModalClose={this.onResultClose}>
            <ModalHeader>{resultTitle}</ModalHeader>
            <ModalBody>
              {result ? <WishlistPreviewTable preview={result} /> : null}
            </ModalBody>
            <ModalFooter>
              <Button onPress={this.onResultClose}>{'Close'}</Button>
            </ModalFooter>
          </ModalContent>
        </Modal>
      </FieldSet>
    );
  }
}

export default TalearrWishlists;
