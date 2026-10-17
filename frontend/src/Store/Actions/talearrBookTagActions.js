import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { set } from './baseActions';
import createHandleActions from './Creators/createHandleActions';

// TaleArr: book tags. Chaptarr tags authors only; the book links come from
// /talearr/booktags (talearr.db) and are merged into books by bookId for display and filters.

//
// Variables

export const section = 'talearrBookTags';

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  isSaving: false,
  error: null,
  saveError: null,
  byBookId: {}
};

//
// Actions Types

export const FETCH_TALEARR_BOOK_TAGS = 'talearrBookTags/fetchTalearrBookTags';
export const SAVE_TALEARR_BOOK_TAGS = 'talearrBookTags/saveTalearrBookTags';
export const SAVE_TALEARR_BOOK_TAGS_EDITOR = 'talearrBookTags/saveTalearrBookTagsEditor';

//
// Action Creators

export const fetchTalearrBookTags = createThunk(FETCH_TALEARR_BOOK_TAGS);
export const saveTalearrBookTags = createThunk(SAVE_TALEARR_BOOK_TAGS);
export const saveTalearrBookTagsEditor = createThunk(SAVE_TALEARR_BOOK_TAGS_EDITOR);

//
// Helpers

function mergeResults(getState, results) {
  const byBookId = { ...getState()[section].byBookId };

  results.forEach(({ bookId, tags = [] }) => {
    if (tags.length) {
      byBookId[bookId] = tags;
    } else {
      delete byBookId[bookId];
    }
  });

  return byBookId;
}

function saveHandler(getState, dispatch, request) {
  dispatch(set({ section, isSaving: true }));

  const promise = createAjaxRequest(request).request;

  promise.done((data) => {
    const results = Array.isArray(data) ? data : [data];

    dispatch(set({
      section,
      isSaving: false,
      saveError: null,
      byBookId: mergeResults(getState, results)
    }));
  });

  promise.fail((xhr) => {
    dispatch(set({ section, isSaving: false, saveError: xhr }));
  });

  return promise;
}

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_TALEARR_BOOK_TAGS]: function(getState, payload, dispatch) {
    dispatch(set({ section, isFetching: true }));

    const promise = createAjaxRequest({
      url: '/talearr/booktags',
      dataType: 'json'
    }).request;

    promise.done((data) => {
      const byBookId = {};

      (data || []).forEach(({ bookId, tags }) => {
        byBookId[bookId] = tags || [];
      });

      dispatch(set({
        section,
        isFetching: false,
        isPopulated: true,
        error: null,
        byBookId
      }));
    });

    promise.fail((xhr) => {
      dispatch(set({ section, isFetching: false, error: xhr }));
    });
  },

  [SAVE_TALEARR_BOOK_TAGS]: function(getState, { bookId, tags }, dispatch) {
    return saveHandler(getState, dispatch, {
      url: `/talearr/booktags/${bookId}`,
      method: 'PUT',
      data: JSON.stringify({ tags }),
      dataType: 'json'
    });
  },

  [SAVE_TALEARR_BOOK_TAGS_EDITOR]: function(getState, { bookIds, tags, applyTags }, dispatch) {
    return saveHandler(getState, dispatch, {
      url: '/talearr/booktags/editor',
      method: 'PUT',
      data: JSON.stringify({ bookIds, tags, applyTags }),
      dataType: 'json'
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({}, defaultState, section);
