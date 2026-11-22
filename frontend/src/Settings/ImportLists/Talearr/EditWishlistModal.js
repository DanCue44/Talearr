import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { readError } from './wishlistFormat';
import WishlistPreviewTable from './WishlistPreviewTable';
import styles from './TalearrWishlists.css';

const sourceOptions = [
  { key: 'upload', value: 'Upload a CSV export' },
  { key: 'path', value: 'CSV file on the server' },
  { key: 'hardcoverList', value: 'Hardcover list' },
  { key: 'audibleLibrary', value: 'Owned Audible library' },
  { key: 'authorList', value: 'Author names' },
  { key: 'urlList', value: 'List from a URL' }
];

const authorListStorage = [
  { key: 'paste', value: 'Paste the names' },
  { key: 'path', value: 'Text file on the server' }
];

const authorMonitorOptions = [
  { key: 'future', value: 'New books only' },
  { key: 'all', value: 'All books' },
  { key: 'none', value: 'None' }
];

const audibleLibraryStorage = [
  { key: 'upload', value: 'Upload the export' },
  { key: 'path', value: 'File on the server' }
];

function initialForm(wishlist) {
  if (!wishlist) {
    return {
      name: '',
      source: 'upload',
      path: '',
      url: '',
      csv: null,
      fileName: null,
      audiobook: true,
      ebook: false,
      searchOnAdd: true,
      toReadOnly: true,
      enabled: true,
      addUnmonitored: true,
      storage: 'upload',
      text: '',
      authorMonitor: 'future'
    };
  }

  let source = wishlist.uploaded ? 'upload' : 'path';
  if (['hardcoverList', 'audibleLibrary', 'authorList', 'urlList'].includes(wishlist.kind)) {
    source = wishlist.kind;
  }

  let storage = wishlist.uploaded ? 'upload' : 'path';
  if (wishlist.kind === 'authorList' && wishlist.uploaded) {
    storage = 'paste';
  }

  return {
    name: wishlist.name,
    source,
    path: wishlist.uploaded ? '' : (wishlist.path || ''),
    url: wishlist.url || '',
    csv: null,
    fileName: null,
    audiobook: wishlist.audiobook,
    ebook: wishlist.ebook,
    searchOnAdd: wishlist.searchOnAdd,
    toReadOnly: wishlist.toReadOnly,
    enabled: wishlist.enabled,
    addUnmonitored: wishlist.addUnmonitored,
    storage,
    text: '',
    authorMonitor: wishlist.authorMonitor || 'future'
  };
}

function usesPaste(form) {
  return form.source === 'authorList' && form.storage === 'paste';
}

function hasAuthors(form) {
  return form.source === 'authorList' || form.source === 'urlList';
}

function usesUpload(form) {
  return form.source === 'upload' || (form.source === 'audibleLibrary' && form.storage === 'upload');
}

function usesPath(form) {
  return form.source === 'path' || ((form.source === 'audibleLibrary' || form.source === 'authorList') && form.storage === 'path');
}

function toKind(source) {
  if (['hardcoverList', 'audibleLibrary', 'authorList', 'urlList'].includes(source)) {
    return source;
  }

  return 'file';
}

function toRequest(form) {
  return {
    name: form.name,
    kind: toKind(form.source),
    path: usesPath(form) ? form.path : null,
    url: form.source === 'hardcoverList' || form.source === 'urlList' ? form.url : null,
    csv: usesUpload(form) ? form.csv : null,
    text: usesPaste(form) && form.text ? form.text : null,
    addUnmonitored: form.addUnmonitored,
    authorMonitor: form.authorMonitor,
    audiobook: form.audiobook,
    ebook: form.ebook,
    searchOnAdd: form.searchOnAdd,
    toReadOnly: form.toReadOnly,
    enabled: form.enabled
  };
}

// Talearr: add or edit a wishlist, with a dry run before saving (docs/TALEARR_WISHLISTS.md).
class EditWishlistModal extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      form: initialForm(props.wishlist),
      isPreviewing: false,
      isSaving: false,
      preview: null,
      error: null
    };
  }

  componentDidUpdate(prevProps) {
    if (this.props.isOpen && !prevProps.isOpen) {
      this.setState({ form: initialForm(this.props.wishlist), preview: null, error: null });
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: value }, preview: null });
  };

  onFileChange = (event) => {
    const file = event.target.files && event.target.files[0];

    if (!file) {
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      const name = this.state.form.name || file.name.replace(/\.(csv|txt)$/i, '');
      const loaded = usesPaste(this.state.form) ?
        { text: reader.result } :
        { csv: reader.result };

      this.setState({ form: { ...this.state.form, ...loaded, fileName: file.name, name }, preview: null, error: null });
    };
    reader.readAsText(file);
  };

  onPreviewPress = () => {
    const { wishlist } = this.props;
    const { form } = this.state;
    const unchangedSaved = wishlist && !form.csv && !form.text && (usesUpload(form) || usesPaste(form));

    this.setState({ isPreviewing: true, error: null });

    const { request } = unchangedSaved ?
      createAjaxRequest({ url: `/talearr/wishlists/${wishlist.id}/preview` }) :
      createAjaxRequest({
        url: '/talearr/wishlists/preview',
        method: 'POST',
        dataType: 'json',
        contentType: 'application/json',
        data: JSON.stringify(toRequest(form))
      });

    request.done((preview) => {
      this.setState({ isPreviewing: false, preview });
    });

    request.fail((xhr) => {
      this.setState({ isPreviewing: false, error: readError(xhr, 'Unable to read the list.') });
    });
  };

  onSavePress = () => {
    const { wishlist } = this.props;

    this.setState({ isSaving: true, error: null });

    const { request } = createAjaxRequest({
      url: wishlist ? `/talearr/wishlists/${wishlist.id}` : '/talearr/wishlists',
      method: wishlist ? 'PUT' : 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(toRequest(this.state.form))
    });

    request.done((saved) => {
      this.setState({ isSaving: false });
      this.props.onSaved(saved);
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, error: readError(xhr, 'Unable to save the wishlist.') });
    });
  };

  //
  // Render

  render() {
    const { isOpen, wishlist, onModalClose } = this.props;
    const { form, isPreviewing, isSaving, preview, error } = this.state;
    const savedUpload = !!(wishlist && wishlist.uploaded);
    const needsFile = (usesUpload(form) && !form.csv && !savedUpload) || (usesPaste(form) && !form.text.trim() && !savedUpload);
    const owned = form.source === 'audibleLibrary';
    const ownedUnmonitored = owned && form.addUnmonitored;

    return (
      <Modal
        isOpen={isOpen}
        size={sizes.EXTRA_LARGE}
        onModalClose={onModalClose}
      >
        <ModalContent onModalClose={onModalClose}>
          <ModalHeader>
            {wishlist ? 'Edit wishlist' : 'Add wishlist'}
          </ModalHeader>

          <ModalBody>
            <Form>
              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>{'Source'}</FormLabel>
                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="source"
                  values={sourceOptions}
                  value={form.source}
                  helpText="Goodreads (My Books > Import and export), StoryGraph (Manage account > Export StoryGraph library), an Audible wishlist exported with audible-cli, or any CSV with a title column. Hardcover lists need the Hardcover API token in Settings > Metadata."
                  onChange={this.onInputChange}
                />
              </FormGroup>

              {
                owned ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Export'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="storage"
                      values={audibleLibraryStorage}
                      value={form.storage}
                      helpText="Export your library with audible-cli (audible library export --format csv), or use a plain list of ASINs, one per line. Talearr never signs in to Audible."
                      onChange={this.onInputChange}
                    />
                  </FormGroup> :
                  null
              }

              {
                form.source === 'authorList' ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Names'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="storage"
                      values={authorListStorage}
                      value={form.storage}
                      helpText="One author name per line. Each name is looked up in the metadata; names that match no author are listed in the preview and the log."
                      onChange={this.onInputChange}
                    />
                  </FormGroup> :
                  null
              }

              {
                usesPaste(form) ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Authors'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.TEXT_AREA}
                      name="text"
                      value={form.text}
                      helpText={savedUpload && !form.text ? 'Leave empty to keep the names saved before, or load a text file' : 'Or load a text file below'}
                      onChange={this.onInputChange}
                    />
                    <div className={styles.fileInput}>
                      <input
                        type="file"
                        accept=".txt,text/plain"
                        onChange={this.onFileChange}
                      />
                    </div>
                  </FormGroup> :
                  null
              }

              {
                usesUpload(form) ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{owned ? 'File' : 'CSV file'}</FormLabel>
                    <div className={styles.fileInput}>
                      <input
                        type="file"
                        accept=".csv,text/csv,text/plain"
                        onChange={this.onFileChange}
                      />
                      <span className={styles.sub}>
                        {form.fileName || (wishlist && wishlist.uploaded ? 'Keeps the file uploaded before unless you pick a new one' : 'Pick the exported file')}
                      </span>
                    </div>
                  </FormGroup> :
                  null
              }

              {
                usesPath(form) ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Path'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="path"
                      value={form.path}
                      helpText="Read again on every sync, so you can replace the file with a newer export, for example /config/wishlists/goodreads.csv"
                      onChange={this.onInputChange}
                    />
                  </FormGroup> :
                  null
              }

              {
                form.source === 'urlList' ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'List URL'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="url"
                      value={form.url}
                      helpText="Read on every sync. Plain text with one author name, ISBN or ASIN per line, or JSON: a list of names, or objects with name, isbn, asin or title."
                      onChange={this.onInputChange}
                    />
                  </FormGroup> :
                  null
              }

              {
                hasAuthors(form) ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Monitor'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="authorMonitor"
                      values={authorMonitorOptions}
                      value={form.authorMonitor}
                      helpText="Which books of each added author are monitored. Authors already in the library are left as they are."
                      onChange={this.onInputChange}
                    />
                  </FormGroup> :
                  null
              }

              {
                form.source === 'hardcoverList' ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'List URL'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="url"
                      value={form.url}
                      helpText="https://hardcover.app/@you/lists/list-name. Want to Read, Owned and Read are the Hardcover Library import list's job."
                      onChange={this.onInputChange}
                    />
                  </FormGroup> :
                  null
              }

              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>{'Name'}</FormLabel>
                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="name"
                  value={form.name}
                  onChange={this.onInputChange}
                />
              </FormGroup>

              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>{'Audiobooks'}</FormLabel>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="audiobook"
                  value={form.audiobook}
                  helpText={ownedUnmonitored ? 'Add the audiobook of each book in the library' : 'Monitor the audiobook of each book on the list'}
                  onChange={this.onInputChange}
                />
              </FormGroup>

              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>{'Ebooks'}</FormLabel>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="ebook"
                  value={form.ebook}
                  helpText={ownedUnmonitored ? 'Add the ebook of each book in the library' : 'Monitor the ebook of each book on the list'}
                  onChange={this.onInputChange}
                />
              </FormGroup>

              {
                owned ?
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Add without monitoring'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="addUnmonitored"
                      value={form.addUnmonitored}
                      helpText="You already own these books: add them to the library unmonitored, so they are never searched. Books already in the library are left as they are."
                      onChange={this.onInputChange}
                    />
                  </FormGroup> :
                  null
              }

              {
                ownedUnmonitored ?
                  null :
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Search when added'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="searchOnAdd"
                      value={form.searchOnAdd}
                      helpText={hasAuthors(form) ? 'For authors: only when monitoring all books' : undefined}
                      onChange={this.onInputChange}
                    />
                  </FormGroup>
              }

              {
                owned || hasAuthors(form) ?
                  null :
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Only to-read rows'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.CHECK}
                      name="toReadOnly"
                      value={form.toReadOnly}
                      helpText="For exports with a shelf or read status (Goodreads, StoryGraph): only the to-read rows"
                      onChange={this.onInputChange}
                    />
                  </FormGroup>
              }

              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>{'Sync automatically'}</FormLabel>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="enabled"
                  value={form.enabled}
                  helpText="Check for new rows every 12 hours. Each row is added once, and only that book is monitored."
                  onChange={this.onInputChange}
                />
              </FormGroup>
            </Form>

            {
              error ?
                <Alert kind={kinds.DANGER}>{error}</Alert> :
                null
            }

            {
              preview ?
                <WishlistPreviewTable preview={preview} /> :
                null
            }
          </ModalBody>

          <ModalFooter>
            <SpinnerButton
              isSpinning={isPreviewing}
              isDisabled={needsFile || isSaving}
              onPress={this.onPreviewPress}
            >
              {'Preview'}
            </SpinnerButton>

            <Button onPress={onModalClose}>
              {'Cancel'}
            </Button>

            <SpinnerButton
              kind={kinds.PRIMARY}
              isSpinning={isSaving}
              isDisabled={needsFile || isPreviewing}
              onPress={this.onSavePress}
            >
              {'Save'}
            </SpinnerButton>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

EditWishlistModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  wishlist: PropTypes.object,
  onSaved: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default EditWishlistModal;
