/* eslint max-params: 0 */
import { push } from 'connected-react-router';
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { toggleBooksMonitored } from 'Store/Actions/bookActions';
import { clearBookFiles, fetchBookFiles } from 'Store/Actions/bookFileActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { clearEditions, fetchEditions } from 'Store/Actions/editionActions';
import { clearQueueDetails, fetchQueueDetails } from 'Store/Actions/queueActions';
import { cancelFetchReleases, clearReleases } from 'Store/Actions/releaseActions';
import createAllAuthorSelector from 'Store/Selectors/createAllAuthorsSelector';
import createCommandsSelector from 'Store/Selectors/createCommandsSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import { findCommand, isCommandExecuting } from 'Utilities/Command';
import { registerPagePopulator, unregisterPagePopulator } from 'Utilities/pagePopulator';
import BookDetails from './BookDetails';

const selectBookFiles = createSelector(
  (state) => state.bookFiles,
  (bookFiles) => {
    const {
      items,
      isFetching,
      isPopulated,
      error
    } = bookFiles;

    const hasBookFiles = !!items.length;

    return {
      isBookFilesFetching: isFetching,
      isBookFilesPopulated: isPopulated,
      bookFilesError: error,
      hasBookFiles,
      bookFiles: items
    };
  }
);

function getMediaType(book) {
  return (book.mediaType || '').toString().toLowerCase() === 'ebook' ? 'ebook' : 'audiobook';
}

// Talearr: the other format of the same work (same author and foreignBookId), preferring a
// row with files, then a monitored one, then the oldest.
function findOtherFormatBook(items, book) {
  if (!book.foreignBookId) {
    return undefined;
  }

  const mediaType = getMediaType(book);
  const isMonitored = (b) => (getMediaType(b) === 'ebook' ? b.ebookMonitored : b.audiobookMonitored);
  const hasFiles = (b) => !!b.hasFiles || (b.statistics?.bookFileCount || 0) > 0;

  return items
    .filter((b) => b.authorId === book.authorId &&
      b.foreignBookId === book.foreignBookId &&
      getMediaType(b) !== mediaType)
    .sort((left, right) => (
      (Number(hasFiles(right)) - Number(hasFiles(left))) ||
      (Number(!!isMonitored(right)) - Number(!!isMonitored(left))) ||
      (left.id - right.id)
    ))[0];
}

function createMapStateToProps() {
  return createSelector(
    (state, { bookId }) => bookId,
    selectBookFiles,
    (state) => state.books,
    (state) => state.editions,
    createAllAuthorSelector(),
    createCommandsSelector(),
    createUISettingsSelector(),
    createDimensionsSelector(),
    (bookId, bookFiles, books, editions, authors, commands, uiSettings, dimensions) => {
      try {
        const book = books.items.find((b) => b.id === bookId);

        if (!book) {
          return {};
        }

        const author = authors.find((a) => a.id === book.authorId);

        if (!author) {
          return {};
        }

        const selectedMediaType = (book.mediaType || '').toString().toLowerCase();
        const otherFormatBook = findOtherFormatBook(books.items, book);
        const ebookRow = selectedMediaType === 'ebook' ? book : otherFormatBook;
        const sortedBooks = books.items.filter((b) => {
          if (b.authorId !== book.authorId) {
            return false;
          }

          if (!selectedMediaType) {
            return true;
          }

          return (b.mediaType || '').toString().toLowerCase() === selectedMediaType;
        });
        sortedBooks.sort((a, b) => ((a.releaseDate > b.releaseDate) ? 1 : -1));
        const bookIndex = sortedBooks.findIndex((b) => b.id === book.id);

        const {
          isBookFilesFetching,
          isBookFilesPopulated,
          bookFilesError,
          hasBookFiles,
          bookFiles: bookFileItems
        } = bookFiles;

        const previousBook = sortedBooks[bookIndex - 1] || _.last(sortedBooks) || book;
        const nextBook = sortedBooks[bookIndex + 1] || _.first(sortedBooks) || book;
        const hasBookNavigation = sortedBooks.length > 1 && bookIndex !== -1;
        const isRefreshingCommand = findCommand(commands, { name: commandNames.REFRESH_BOOK });
        const isRefreshing = (
          isRefreshingCommand &&
        isCommandExecuting(isRefreshingCommand) &&
        isRefreshingCommand.body &&
        isRefreshingCommand.body.bookId === book.id
        );
        const isSearchingCommand = findCommand(commands, { name: commandNames.BOOK_SEARCH });
        const isSearching = (
          isSearchingCommand &&
        isCommandExecuting(isSearchingCommand) &&
        isSearchingCommand.body &&
        isSearchingCommand.body.bookIds &&
        isSearchingCommand.body.bookIds.indexOf(book.id) > -1
        );
        const isRenamingFiles = isCommandExecuting(findCommand(commands, { name: commandNames.RENAME_FILES, authorId: author.id }));
        const isRenamingAuthorCommand = findCommand(commands, { name: commandNames.RENAME_AUTHOR });
        const isRenamingAuthor = (
          isRenamingAuthorCommand &&
        isCommandExecuting(isRenamingAuthorCommand) &&
        isRenamingAuthorCommand.body &&
        isRenamingAuthorCommand.body.authorIds &&
        isRenamingAuthorCommand.body.authorIds.indexOf(author.id) > -1
        );

        // Talearr: Convert to M4B for this book.
        const isConvertingToM4bCommand = findCommand(commands, { name: commandNames.TALEARR_CONVERT_TO_M4B, bookId: book.id });
        const isConvertingToM4b = isCommandExecuting(isConvertingToM4bCommand);

        const isFetching = isBookFilesFetching || editions.isFetching;
        const isPopulated = isBookFilesPopulated && editions.isPopulated;
        const selectedEdition = editions.items
          .filter((edition) => edition.bookId === bookId && edition.monitored)
          .sort((left, right) => left.id - right.id)[0];
        const chapters = Array.isArray(selectedEdition?.chapters) ?
          selectedEdition.chapters.filter(
            (chapter) =>
              chapter &&
              (
                chapter.title?.trim() ||
                Number(chapter.startOffsetMs) > 0 ||
                Number(chapter.startOffsetSec) > 0 ||
                Number(chapter.lengthMs) > 0
              )
          ) :
          [];

        return {
          ...book,
          shortDateFormat: uiSettings.shortDateFormat,
          author,
          isRefreshing,
          isSearching,
          isRenamingFiles,
          isRenamingAuthor,
          isConvertingToM4b,
          isFetching,
          isPopulated,
          bookFilesError,
          hasBookFiles,
          bookFiles: bookFileItems,
          chapters,
          previousBook,
          nextBook,
          hasBookNavigation,
          otherFormatBookId: otherFormatBook?.id,
          // Talearr: the page shows the ebook's cover for the whole work, like the Books cards,
          // and falls back to the other format's cover when the ebook has none
          workCoverImages: [ebookRow, otherFormatBook].map((row) => row?.images).find((images) => images?.length),
          isSmallScreen: dimensions.isSmallScreen
        };
      } catch (error) {
        console.error('Error in BookDetailsConnector mapStateToProps:', error);
        return {};
      }
    }
  );
}

const mapDispatchToProps = {
  push,
  executeCommand,
  fetchBookFiles,
  clearBookFiles,
  fetchEditions,
  clearEditions,
  fetchQueueDetails,
  clearQueueDetails,
  clearReleases,
  cancelFetchReleases,
  toggleBooksMonitored
};

function getMonitoredEditions(props) {
  return _.map(_.filter(props.editions, { monitored: true }), 'id').sort();
}

class BookDetailsConnector extends Component {

  componentDidMount() {
    registerPagePopulator(this.populate);
    this.populate();
  }

  componentDidUpdate(prevProps) {
    const {
      id,
      anyReleaseOk,
      isRefreshing,
      isRenamingFiles,
      isRenamingAuthor,
      isConvertingToM4b
    } = this.props;

    if (
      (prevProps.isConvertingToM4b && !isConvertingToM4b) ||
      (prevProps.isRefreshing && !isRefreshing) ||
      (prevProps.isRenamingFiles && !isRenamingFiles) ||
      (prevProps.isRenamingAuthor && !isRenamingAuthor) ||
      !_.isEqual(getMonitoredEditions(prevProps), getMonitoredEditions(this.props)) ||
      (prevProps.anyReleaseOk === false && anyReleaseOk === true)
    ) {
      this.unpopulate();
      this.populate();
    }

    // If the id has changed we need to clear the book
    // files and fetch from the server.

    if (prevProps.id !== id) {
      this.unpopulate();
      this.populate();
    }
  }

  componentWillUnmount() {
    unregisterPagePopulator(this.populate);
    this.unpopulate();
  }

  //
  // Control

  populate = () => {
    const bookId = this.props.id;

    this.props.fetchBookFiles({ bookId });
    this.props.fetchEditions({ bookId });
    this.props.fetchQueueDetails({ bookIds: [bookId] });
  };

  unpopulate = () => {
    this.props.cancelFetchReleases();
    this.props.clearReleases();
    this.props.clearBookFiles();
    this.props.clearEditions();
    this.props.clearQueueDetails();
  };

  //
  // Listeners

  onMonitorTogglePress = (monitored) => {
    this.props.toggleBooksMonitored({
      bookIds: [this.props.id],
      monitored
    });
  };

  onRefreshPress = () => {
    this.props.executeCommand({
      name: commandNames.REFRESH_BOOK,
      bookId: this.props.id
    });
  };

  onSearchPress = () => {
    this.props.executeCommand({
      name: commandNames.BOOK_SEARCH,
      bookIds: [this.props.id]
    });
  };

  onConvertToM4bPress = () => {
    this.props.executeCommand({
      name: commandNames.TALEARR_CONVERT_TO_M4B,
      bookId: this.props.id
    });
  };

  onFormatChange = (mediaType) => {
    const { otherFormatBookId } = this.props;
    const current = (this.props.mediaType || '').toString().toLowerCase() === 'ebook' ? 'ebook' : 'audiobook';

    if (mediaType !== current && otherFormatBookId) {
      this.props.push(`${window.Chaptarr.urlBase}/book/${otherFormatBookId}`);
    }
  };

  //
  // Render

  render() {
    return (
      <BookDetails
        {...this.props}
        onMonitorTogglePress={this.onMonitorTogglePress}
        onRefreshPress={this.onRefreshPress}
        onSearchPress={this.onSearchPress}
        onConvertToM4bPress={this.onConvertToM4bPress}
        onFormatChange={this.onFormatChange}
      />
    );
  }
}

BookDetailsConnector.propTypes = {
  id: PropTypes.number,
  mediaType: PropTypes.string,
  otherFormatBookId: PropTypes.number,
  push: PropTypes.func.isRequired,
  anyReleaseOk: PropTypes.bool,
  isRefreshing: PropTypes.bool.isRequired,
  isRenamingFiles: PropTypes.bool.isRequired,
  isRenamingAuthor: PropTypes.bool.isRequired,
  isConvertingToM4b: PropTypes.bool,
  isBookFetching: PropTypes.bool,
  isBookPopulated: PropTypes.bool,
  bookId: PropTypes.number.isRequired,
  fetchBookFiles: PropTypes.func.isRequired,
  clearBookFiles: PropTypes.func.isRequired,
  fetchEditions: PropTypes.func.isRequired,
  clearEditions: PropTypes.func.isRequired,
  fetchQueueDetails: PropTypes.func.isRequired,
  clearQueueDetails: PropTypes.func.isRequired,
  clearReleases: PropTypes.func.isRequired,
  cancelFetchReleases: PropTypes.func.isRequired,
  toggleBooksMonitored: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookDetailsConnector);
