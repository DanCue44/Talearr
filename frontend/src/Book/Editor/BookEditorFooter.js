import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import SelectInput from 'Components/Form/SelectInput';
import SpinnerButton from 'Components/Link/SpinnerButton';
import PageContentFooter from 'Components/Page/PageContentFooter';
import { kinds } from 'Helpers/Props';
import { saveTalearrBookTagsEditor } from 'Store/Actions/talearrBookTagActions';
import translate from 'Utilities/String/translate';
import BookEditorFooterLabel from './BookEditorFooterLabel';
import DeleteBookModal from './Delete/DeleteBookModal';
import BookTagsModal from './Tags/BookTagsModal';
import styles from './BookEditorFooter.css';

const NO_CHANGE = 'noChange';

class BookEditorFooter extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      monitored: NO_CHANGE,
      rootFolderPath: NO_CHANGE,
      savingTags: false,
      isDeleteBookModalOpen: false,
      isTagsModalOpen: false,
      isConfirmMoveModalOpen: false,
      destinationRootFolder: null
    };
  }

  componentDidUpdate(prevProps) {
    const {
      isSaving,
      saveError
    } = this.props;

    if (prevProps.isSaving && !isSaving && !saveError) {
      this.setState({
        monitored: NO_CHANGE,
        rootFolderPath: NO_CHANGE,
        savingTags: false
      });
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value });

    if (value === NO_CHANGE) {
      return;
    }

    switch (name) {
      case 'monitored':
        this.props.onSaveSelected({ [name]: value === 'monitored' });
        break;
      default:
        this.props.onSaveSelected({ [name]: value });
    }
  };

  // Talearr: book tags (stored by Talearr, Chaptarr only tags authors).
  onTagsPress = () => {
    this.setState({ isTagsModalOpen: true });
  };

  onTagsModalClose = () => {
    this.setState({ isTagsModalOpen: false });
  };

  onApplyTagsPress = (tags, applyTags) => {
    this.setState({ isTagsModalOpen: false });

    this.props.dispatchSaveTalearrBookTagsEditor({
      bookIds: this.props.bookIds,
      tags,
      applyTags
    });
  };

  onDeleteSelectedPress = () => {
    this.setState({ isDeleteBookModalOpen: true });
  };

  onDeleteBookModalClose = () => {
    this.setState({ isDeleteBookModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      bookIds,
      selectedCount,
      isSaving,
      isDeleting,
      isSavingTags
    } = this.props;

    const {
      monitored,
      isDeleteBookModalOpen,
      isTagsModalOpen
    } = this.state;

    const monitoredOptions = [
      { key: NO_CHANGE, value: translate('NoChange'), isDisabled: true },
      { key: 'monitored', value: translate('Monitored') },
      { key: 'unmonitored', value: translate('Unmonitored') }
    ];

    return (
      <PageContentFooter>
        <div className={styles.inputContainer}>
          <BookEditorFooterLabel
            label={translate('MonitorBook')}
            isSaving={isSaving && monitored !== NO_CHANGE}
          />

          <SelectInput
            name="monitored"
            value={monitored}
            values={monitoredOptions}
            isDisabled={!selectedCount}
            onChange={this.onInputChange}
          />
        </div>

        <div className={styles.buttonContainer}>
          <div className={styles.buttonContainerContent}>
            <BookEditorFooterLabel
              label={translate('SelectedCountBooksSelectedInterp', [selectedCount])}
              isSaving={false}
            />

            <div className={styles.buttons}>
              <SpinnerButton
                className={styles.tagsButton}
                isSpinning={isSavingTags}
                isDisabled={!selectedCount || isSavingTags}
                onPress={this.onTagsPress}
              >
                {'Set Book Tags'}
              </SpinnerButton>

              <SpinnerButton
                className={styles.deleteSelectedButton}
                kind={kinds.DANGER}
                isSpinning={isDeleting}
                isDisabled={!selectedCount || isDeleting}
                onPress={this.onDeleteSelectedPress}
              >
                {translate('Delete')}
              </SpinnerButton>
            </div>
          </div>
        </div>

        <DeleteBookModal
          isOpen={isDeleteBookModalOpen}
          bookIds={bookIds}
          onModalClose={this.onDeleteBookModalClose}
        />

        <BookTagsModal
          isOpen={isTagsModalOpen}
          bookIds={bookIds}
          onApplyTagsPress={this.onApplyTagsPress}
          onModalClose={this.onTagsModalClose}
        />

      </PageContentFooter>
    );
  }
}

BookEditorFooter.propTypes = {
  bookIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  selectedCount: PropTypes.number.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isDeleting: PropTypes.bool.isRequired,
  deleteError: PropTypes.object,
  onSaveSelected: PropTypes.func.isRequired,
  isSavingTags: PropTypes.bool.isRequired,
  dispatchSaveTalearrBookTagsEditor: PropTypes.func.isRequired
};

function mapStateToProps(state) {
  return {
    isSavingTags: state.talearrBookTags.isSaving
  };
}

const mapDispatchToProps = {
  dispatchSaveTalearrBookTagsEditor: saveTalearrBookTagsEditor
};

export default connect(mapStateToProps, mapDispatchToProps)(BookEditorFooter);
