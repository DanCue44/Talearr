/* eslint max-params: 0 */
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import createBookAuthorSelector from 'Store/Selectors/createBookAuthorSelector';
import createBookQualityProfileSelector from 'Store/Selectors/createBookQualityProfileSelector';
import createBookSelector from 'Store/Selectors/createBookSelector';
import createExecutingCommandsSelector from 'Store/Selectors/createExecutingCommandsSelector';

function selectShowSearchAction() {
  return createSelector(
    (state) => state.bookIndex,
    (bookIndex) => {
      const view = bookIndex.view;

      switch (view) {
        case 'posters':
          return bookIndex.posterOptions.showSearchAction;
        case 'banners':
          return bookIndex.bannerOptions.showSearchAction;
        case 'overview':
          return bookIndex.overviewOptions.showSearchAction;
        default:
          return bookIndex.tableOptions.showSearchAction;
      }
    }
  );
}

// Talearr: a combined item names each format's quality profile ("E-Book / Audiobook")
function getGroupQualityProfile(state, author, formats, fallback) {
  const profiles = state.settings.qualityProfiles.items;
  const names = (formats || []).map(({ mediaType }) => {
    const id = mediaType === 'ebook' ? author?.ebookQualityProfileId : author?.audiobookQualityProfileId;

    return profiles.find((profile) => profile.id === id)?.name;
  }).filter(Boolean);
  const unique = [...new Set(names)];

  return unique.length ? { ...fallback, name: unique.join(' / ') } : fallback;
}

function getGroupRows(state, groupBookIds) {
  if (!groupBookIds || groupBookIds.length < 2) {
    return null;
  }

  const { items, itemMap } = state.books;

  return groupBookIds.map((id) => items[itemMap[id]]).filter(Boolean);
}

function mergeServerGroupFields(book) {
  const formats = book.workFormats;
  const cover = book.workImages?.length ? { images: book.workImages } : {};

  return {
    ...cover,
    audiobookMonitored: formats.some((f) => f.mediaType === 'audiobook' && f.monitored),
    ebookMonitored: formats.some((f) => f.mediaType === 'ebook' && f.monitored),
    hasFiles: formats.some((f) => f.hasFile),
    statistics: {
      ...book.statistics,
      ...book.groupStatistics
    }
  };
}

function mergeGroupFields(book, rows) {
  const sum = (key) => rows.reduce((total, row) => total + (row.statistics?.[key] || 0), 0);
  // Talearr: combined cards show the ebook's cover when the work has one
  const ebook = rows.find((row) => row.mediaType === 'ebook' && row.images?.length);
  const cover = ebook ? { images: ebook.images, remoteCover: ebook.remoteCover } : {};

  return {
    ...cover,
    audiobookMonitored: rows.some((row) => row.audiobookMonitored),
    ebookMonitored: rows.some((row) => row.ebookMonitored),
    hasFiles: rows.some((row) => row.hasFiles),
    statistics: {
      ...book.statistics,
      bookCount: sum('bookCount'),
      bookFileCount: sum('bookFileCount'),
      totalBookCount: sum('totalBookCount'),
      sizeOnDisk: sum('sizeOnDisk')
    }
  };
}

function createMapStateToProps() {
  // Create per-instance selectors once to maintain memoization
  const selectBook = createBookSelector();
  const selectAuthor = createBookAuthorSelector();
  const selectQualityProfile = createBookQualityProfileSelector();
  const selectShowSearch = selectShowSearchAction();
  const selectExecutingCommands = createExecutingCommandsSelector();
  
  // Stable empty result to avoid prop shape changes
  const EMPTY_RESULT = {};
  const EMPTY_TAGS = [];

  const extractAuthorName = (authorTitle, title) => {
    if (!authorTitle) {
      return '';
    }

    if (!title) {
      return authorTitle;
    }

    const suffix = ` ${title}`;
    return authorTitle.endsWith(suffix) ? authorTitle.slice(0, -suffix.length) : authorTitle;
  };

  return (state, ownProps) => {
    // Get book from props or state (compute only once)
    const bookId = ownProps.bookId ?? ownProps.id;
    const book = ownProps.book || (bookId != null ? selectBook(state, { bookId }) : undefined);
    
    if (!book) {
      return EMPTY_RESULT;
    }
    
    // Get dependent data using cached selectors
    const authorFromStore = selectAuthor(state, { authorId: book.authorId });
    const author = book.author ?? authorFromStore ?? {
      id: book.authorId,
      authorName: extractAuthorName(book.authorTitle, book.title),
      path: '',
      titleSlug: undefined
    };
    const qualityProfile = selectQualityProfile(state, { bookId: book.id });
    const showSearchAction = selectShowSearch(state, ownProps);
    const executingCommands = selectExecutingCommands(state);
    
    // Safe command checking with proper guards
    const commands = Array.isArray(executingCommands) ? executingCommands : [];
    
    const isRefreshingBook = commands.some((command) => {
      return (
        (command.name === commandNames.REFRESH_AUTHOR &&
          command.body?.authorId === book.authorId) ||
        (command.name === commandNames.REFRESH_BOOK &&
          command.body?.bookId === book.id)
      );
    });
    
    const isSearchingBook = commands.some((command) => {
      if (command.name === commandNames.AUTHOR_SEARCH) {
        return command.body?.authorId === book.authorId;
      }
      if (command.name === commandNames.BOOK_SEARCH) {
        const ids = command.body?.bookIds;
        return Array.isArray(ids) && ids.includes(book.id);
      }
      return false;
    });
    
    // Talearr: a combined All formats item reports its whole work, from the rows in the books
    // store (client-side views) or from the grouped server page (infinite posters)
    const serverGroup = ownProps.groupBookIds && book.workFormats?.length > 1;
    const groupRows = serverGroup ? null : getGroupRows(state, ownProps.groupBookIds);
    let groupFields = null;

    if (serverGroup) {
      groupFields = mergeServerGroupFields(book);
    } else if (groupRows) {
      groupFields = mergeGroupFields(book, groupRows);
    }
    const groupQualityProfile = (serverGroup || groupRows) ?
      getGroupQualityProfile(state, author, ownProps.formats, qualityProfile) :
      qualityProfile;

    // Component expects flattened book fields via spread
    return {
      ...book,
      ...groupFields,
      formats: ownProps.formats,
      author,
      // Talearr: book tags (Tags column) from the Talearr book tags store.
      tags: state.talearrBookTags.byBookId[book.id] || EMPTY_TAGS,
      qualityProfile: groupQualityProfile,
      // Talearr: with All formats listed, cards show which format each book is
      showMediaType: state.bookIndex.selectedMediaType === 'all',
      showSearchAction,
      isRefreshingBook,
      isSearchingBook
    };
  };
}

const mapDispatchToProps = {
  dispatchExecuteCommand: executeCommand
};

class BookIndexItemConnector extends Component {

  //
  // Listeners

  onRefreshBookPress = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.REFRESH_BOOK,
      bookId: this.props.id
    });
  };

  onSearchPress = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.BOOK_SEARCH,
      bookIds: [this.props.id]
    });
  };

  //
  // Render

  render() {
    const {
      id,
      component: ItemComponent,
      ...otherProps
    } = this.props;

    if (!id) {
      return null;
    }

    return (
        <ItemComponent
          {...otherProps}
          id={id}
          onRefreshBookPress={this.onRefreshBookPress}
          onSearchPress={this.onSearchPress}
        />
      );
  }
}

BookIndexItemConnector.propTypes = {
  id: PropTypes.number,
  component: PropTypes.elementType.isRequired,
  dispatchExecuteCommand: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookIndexItemConnector);
