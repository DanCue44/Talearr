import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { fetchBooks, saveBook, setBookValue } from 'Store/Actions/bookActions';
import { saveEditions } from 'Store/Actions/editionActions';
import { saveTalearrBookTags } from 'Store/Actions/talearrBookTagActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createBookSelector from 'Store/Selectors/createBookSelector';
import selectSettings from 'Store/Selectors/selectSettings';
import createAjaxRequest from 'Utilities/createAjaxRequest';
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
        authorId: book.authorId,
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
  dispatchSaveTalearrBookTags: saveTalearrBookTags,
  dispatchFetchBooks: fetchBooks
};

class EditBookModalContentConnector extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    // Talearr: book tags are saved on their own route; null means unchanged.
    // Talearr: the preferred series is saved on its own route too; undefined means unchanged.
    this.state = {
      talearrTags: null,
      talearrSeries: null,
      talearrPreferredSeries: undefined,
      talearrEditionLock: null,
      talearrLocked: undefined,
      talearrTitleDetails: null,
      talearrTitle: undefined
    };

    this._abortSeriesRequest = null;
    this._abortLockRequest = null;
    this._abortTitleRequest = null;
  }

  componentDidMount() {
    this.fetchTalearrSeries();
    this.fetchTalearrEditionLock();
    this.fetchTalearrTitle();
  }

  componentDidUpdate(prevProps, prevState) {
    if (prevProps.isSaving && !this.props.isSaving && !this.props.saveError) {
      // Talearr: the lock is saved after the book, so it takes the edition just picked.
      this.saveTalearrEditionLock();
      this.props.onModalClose();
    }
  }

  componentWillUnmount() {
    if (this._abortSeriesRequest) {
      this._abortSeriesRequest();
      this._abortSeriesRequest = null;
    }

    if (this._abortLockRequest) {
      this._abortLockRequest();
      this._abortLockRequest = null;
    }

    if (this._abortTitleRequest) {
      this._abortTitleRequest();
      this._abortTitleRequest = null;
    }
  }

  //
  // Data

  fetchTalearrSeries = () => {
    const { request, abortRequest } = createAjaxRequest({
      url: `/talearr/preferredseries/${this.props.bookId}`
    });

    this._abortSeriesRequest = abortRequest;

    request.done((data) => {
      this.setState({ talearrSeries: data });
    });
  };

  fetchTalearrEditionLock = () => {
    const { request, abortRequest } = createAjaxRequest({
      url: `/talearr/editionlock/${this.props.bookId}`
    });

    this._abortLockRequest = abortRequest;

    request.done((data) => {
      this.setState({ talearrEditionLock: data });
    });
  };

  // Talearr: the title set here is saved on its own route; undefined means unchanged.
  fetchTalearrTitle = () => {
    const { request, abortRequest } = createAjaxRequest({
      url: `/talearr/titleoverride/${this.props.bookId}`
    });

    this._abortTitleRequest = abortRequest;

    request.done((data) => {
      this.setState({ talearrTitleDetails: data });
    });
  };

  saveTalearrTitle = () => {
    const { talearrTitle, talearrTitleDetails } = this.state;
    const saved = (talearrTitleDetails && talearrTitleDetails.title) || '';

    if (talearrTitle === undefined || talearrTitle.trim() === saved) {
      return;
    }

    const { request } = createAjaxRequest({
      url: `/talearr/titleoverride/${this.props.bookId}`,
      method: 'PUT',
      dataType: 'json',
      data: JSON.stringify({ title: talearrTitle.trim() || null })
    });

    request.done(() => {
      this.props.dispatchFetchBooks({ authorId: this.props.authorId });
    });
  };

  saveTalearrEditionLock = () => {
    const { talearrLocked, talearrEditionLock } = this.state;
    const saved = !!(talearrEditionLock && talearrEditionLock.locked);

    if (talearrLocked === undefined || talearrLocked === saved) {
      return;
    }

    const { request } = createAjaxRequest({
      url: `/talearr/editionlock/${this.props.bookId}`,
      method: 'PUT',
      dataType: 'json',
      data: JSON.stringify({ locked: talearrLocked })
    });

    request.done(() => {
      this.props.dispatchFetchBooks({ authorId: this.props.authorId });
    });
  };

  saveTalearrPreferredSeries = () => {
    const { talearrPreferredSeries, talearrSeries } = this.state;
    const saved = (talearrSeries && talearrSeries.preferredSeries) || '';

    if (talearrPreferredSeries === undefined || talearrPreferredSeries === saved) {
      return;
    }

    const { request } = createAjaxRequest({
      url: `/talearr/preferredseries/${this.props.bookId}`,
      method: 'PUT',
      dataType: 'json',
      data: JSON.stringify({ seriesTitle: talearrPreferredSeries || null })
    });

    request.done(() => {
      this.props.dispatchFetchBooks({ authorId: this.props.authorId });
    });
  };

  //
  // Listeners

  onTalearrPreferredSeriesChange = ({ value }) => {
    this.setState({ talearrPreferredSeries: value });
  };

  onTalearrLockedChange = ({ value }) => {
    this.setState({ talearrLocked: value });
  };

  onTalearrTitleChange = ({ value }) => {
    this.setState({ talearrTitle: value });
  };

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

    this.saveTalearrPreferredSeries();
    this.saveTalearrTitle();

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
    const {
      talearrSeries,
      talearrPreferredSeries,
      talearrEditionLock,
      talearrLocked,
      talearrTitleDetails,
      talearrTitle
    } = this.state;

    const savedTitle = (talearrTitleDetails && talearrTitleDetails.title) || '';

    const savedLocked = !!(talearrEditionLock && talearrEditionLock.locked);

    const savedPreferredSeries = (talearrSeries && talearrSeries.preferredSeries) || '';

    return (
      <EditBookModalContent
        {...this.props}
        talearrTags={this.state.talearrTags || this.props.savedTalearrTags}
        talearrSeriesOptions={talearrSeries ? talearrSeries.options : []}
        talearrPreferredSeries={talearrPreferredSeries === undefined ? savedPreferredSeries : talearrPreferredSeries}
        onTalearrPreferredSeriesChange={this.onTalearrPreferredSeriesChange}
        talearrLocked={talearrLocked === undefined ? savedLocked : talearrLocked}
        onTalearrLockedChange={this.onTalearrLockedChange}
        talearrTitle={talearrTitle === undefined ? savedTitle : talearrTitle}
        talearrTitleDetails={talearrTitleDetails}
        onTalearrTitleChange={this.onTalearrTitleChange}
        onInputChange={this.onInputChange}
        onTalearrTagsChange={this.onTalearrTagsChange}
        onSavePress={this.onSavePress}
      />
    );
  }
}

EditBookModalContentConnector.propTypes = {
  bookId: PropTypes.number,
  authorId: PropTypes.number,
  dispatchFetchBooks: PropTypes.func.isRequired,
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
