import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

// Talearr: the optional, read-only Calibre library used to identify ebooks.

//
// Variables

const section = 'settings.talearrCalibre';

//
// Actions Types

export const FETCH_TALEARR_CALIBRE = 'settings/talearrCalibre/fetchTalearrCalibre';
export const SAVE_TALEARR_CALIBRE = 'settings/talearrCalibre/saveTalearrCalibre';
export const SET_TALEARR_CALIBRE_VALUE = 'settings/talearrCalibre/setTalearrCalibreValue';

//
// Action Creators

export const fetchTalearrCalibre = createThunk(FETCH_TALEARR_CALIBRE);
export const saveTalearrCalibre = createThunk(SAVE_TALEARR_CALIBRE);
export const setTalearrCalibreValue = createAction(SET_TALEARR_CALIBRE_VALUE, (payload) => {
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
    [FETCH_TALEARR_CALIBRE]: createFetchHandler(section, '/config/talearrcalibre'),
    [SAVE_TALEARR_CALIBRE]: createSaveHandler(section, '/config/talearrcalibre')
  },

  //
  // Reducers

  reducers: {
    [SET_TALEARR_CALIBRE_VALUE]: createSetSettingValueReducer(section)
  }

};
