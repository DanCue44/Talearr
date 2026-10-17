import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { saveBook, setBookValue } from 'Store/Actions/bookActions';
import { saveEditions } from 'Store/Actions/editionActions';
import { saveTalearrBookTags } from 'Store/Actions/talearrBookTagActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createBookSelector from 'Store/Selectors/createBookSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import EditBookModalContent from './EditBookModalContent';

function createMapStateToProps() {
  return createSelector(
    (state) => state.books,
    (state) => state.editions,
    createBookSelector(),
    createAuthorSelector(),
    (state, { bookId }) => state.talearrBookTags.byBookId[bookId],
    (bookState, editionState, book, author, talearrTags) => {
      const {
        isSaving,
        saveError,
        pendingChanges
      } = bookState;

      const {
        isFetching,
        isPopulated,
        error
      } = editionState;

      const bookSettings = _.pick(book, [
        'monitored',
        'anyEditionOk'
      ]);
      bookSettings.editions = editionState.items;

      const settings = selectSettings(bookSettings, pendingChanges, saveError);

      return {
        title: book.title,
        authorName: author.authorName,
        bookType: book.bookType,
        statistics: book.statistics,
        isFetching,
        isPopulated,
        error,
        isSaving,
        saveError,
        item: settings.settings,
        savedTalearrTags: talearrTags || [],
        ...settings
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchSetBookValue: setBookValue,
  dispatchSaveBook: saveBook,
  dispatchSaveEditions: saveEditions,
  dispatchSaveTalearrBookTags: saveTalearrBookTags
};

class EditBookModalContentConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    // TaleArr: book tags are saved on their own route; null means unchanged.
    this.state = {
      talearrTags: null
    };
  }

  componentDidUpdate(prevProps, prevState) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      this.props.onModalClose();
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetBookValue({ name, value });
  };

  onTalearrTagsChange = ({ value }) => {
    this.setState({ talearrTags: value });
  };

  onSavePress = () => {
    const { talearrTags } = this.state;

    if (talearrTags && !_.isEqual(_.sortBy(talearrTags), _.sortBy(this.props.savedTalearrTags))) {
      this.props.dispatchSaveTalearrBookTags({
        bookId: this.props.bookId,
        tags: talearrTags
      });
    }

    this.props.dispatchSaveBook({
      id: this.props.bookId
    });
    this.props.dispatchSaveEditions({
      id: this.props.bookId
    });
  };

  //
  // Render

  render() {
    return (
      <EditBookModalContent
        {...this.props}
        talearrTags={this.state.talearrTags || this.props.savedTalearrTags}
        onInputChange={this.onInputChange}
        onTalearrTagsChange={this.onTalearrTagsChange}
        onSavePress={this.onSavePress}
      />
    );
  }
}

EditBookModalContentConnector.propTypes = {
  bookId: PropTypes.number,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  dispatchSetBookValue: PropTypes.func.isRequired,
  dispatchSaveBook: PropTypes.func.isRequired,
  dispatchSaveEditions: PropTypes.func.isRequired,
  savedTalearrTags: PropTypes.arrayOf(PropTypes.number).isRequired,
  dispatchSaveTalearrBookTags: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(EditBookModalContentConnector);
