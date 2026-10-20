import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import { icons, kinds, messageTypes } from 'Helpers/Props';
import { showMessage } from 'Store/Actions/appActions';
import { updateItem } from 'Store/Actions/baseActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';

// Talearr: switch an author between all books and only the user's books
// (docs/TALEARR_TRACKED_AUTHORS.md). Uses /api/v1/talearr/authors/{id}/catalog; the server
// refreshes the author after a change.
const helpText = {
  full: 'Following all books by this author. Switch to keep only the books you added, requested, monitor or have files for.',
  tracked: 'Keeping only your books by this author. Switch to follow all books by this author again.'
};

class TalearrAuthorCatalogButton extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      isConfirmOpen: false,
      isSaving: false
    };
  }

  //
  // Control

  setMode = (mode) => {
    const { authorId, dispatch } = this.props;
    this.setState({ isSaving: true });

    const { request } = createAjaxRequest({
      url: `/talearr/authors/${authorId}/catalog`,
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ mode })
    });

    request.done((result) => {
      this.setState({ isSaving: false, isConfirmOpen: false });
      dispatch(updateItem({ section: 'authors', id: authorId, talearrCatalogMode: result.mode, updateOnly: true }));
      dispatch(showMessage({
        id: `talearr-catalog-${authorId}`,
        name: 'TalearrAuthorCatalog',
        message: result.mode === 'tracked' ?
          'Only your books are kept. The author is refreshing and other catalog books are removed.' :
          'All books by this author are followed again. The author is refreshing.',
        type: messageTypes.SUCCESS,
        hideAfter: 6
      }));
    });

    request.fail(() => {
      this.setState({ isSaving: false, isConfirmOpen: false });
      dispatch(showMessage({
        id: `talearr-catalog-${authorId}`,
        name: 'TalearrAuthorCatalog',
        message: 'Changing which books are kept failed. See System > Logs.',
        type: messageTypes.ERROR,
        hideAfter: 12
      }));
    });
  };

  //
  // Listeners

  onPress = () => {
    if (this.props.catalogMode === 'tracked') {
      this.setMode('full');
      return;
    }

    this.setState({ isConfirmOpen: true });
  };

  onConfirm = () => {
    this.setMode('tracked');
  };

  onCancel = () => {
    this.setState({ isConfirmOpen: false });
  };

  //
  // Render

  render() {
    const { authorName, catalogMode } = this.props;
    const { isConfirmOpen, isSaving } = this.state;
    const isTracked = catalogMode === 'tracked';

    return (
      <>
        <PageToolbarButton
          label={isTracked ? 'Follow all books' : 'Only my books'}
          title={isTracked ? helpText.tracked : helpText.full}
          iconName={isTracked ? icons.BOOK : icons.MONITORED}
          isSpinning={isSaving && !isConfirmOpen}
          isDisabled={isSaving}
          onPress={this.onPress}
        />

        <ConfirmModal
          isOpen={isConfirmOpen}
          kind={kinds.DANGER}
          title="Only my books"
          message={`Keep only the books by ${authorName} that you added, requested, monitor or have files for. The rest of the catalog is removed from Talearr (files on disk are never touched) and new books are not added. You can follow all books again at any time.`}
          confirmLabel="Only my books"
          isSpinning={isSaving}
          onConfirm={this.onConfirm}
          onCancel={this.onCancel}
        />
      </>
    );
  }
}

TalearrAuthorCatalogButton.propTypes = {
  authorId: PropTypes.number.isRequired,
  authorName: PropTypes.string,
  catalogMode: PropTypes.string,
  dispatch: PropTypes.func.isRequired
};

TalearrAuthorCatalogButton.defaultProps = {
  catalogMode: 'full'
};

export default connect()(TalearrAuthorCatalogButton);
