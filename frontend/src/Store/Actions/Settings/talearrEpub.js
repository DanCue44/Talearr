import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

// Talearr: Kindle to EPUB conversion settings (convert AZW3 and MOBI after import).

//
// Variables

const section = 'settings.talearrEpub';

//
// Actions Types

export const FETCH_TALEARR_EPUB = 'settings/talearrEpub/fetchTalearrEpub';
export const SAVE_TALEARR_EPUB = 'settings/talearrEpub/saveTalearrEpub';
export const SET_TALEARR_EPUB_VALUE = 'settings/talearrEpub/setTalearrEpubValue';

//
// Action Creators

export const fetchTalearrEpub = createThunk(FETCH_TALEARR_EPUB);
export const saveTalearrEpub = createThunk(SAVE_TALEARR_EPUB);
export const setTalearrEpubValue = createAction(SET_TALEARR_EPUB_VALUE, (payload) => {
  return {
    section,
    ...payload
  };
});

//
// Details

export default {

  //
  // State

  defaultState: {
    isFetching: false,
    isPopulated: false,
    error: null,
    pendingChanges: {},
    isSaving: false,
    saveError: null,
    item: {}
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_TALEARR_EPUB]: createFetchHandler(section, '/config/talearrepub'),
    [SAVE_TALEARR_EPUB]: createSaveHandler(section, '/config/talearrepub')
  },

  //
  // Reducers

  reducers: {
    [SET_TALEARR_EPUB_VALUE]: createSetSettingValueReducer(section)
  }

};
