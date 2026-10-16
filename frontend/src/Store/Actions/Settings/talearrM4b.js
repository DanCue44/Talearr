import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

// TaleArr: M4B merge settings (convert multi-file audiobooks after import).

//
// Variables

const section = 'settings.talearrM4b';

//
// Actions Types

export const FETCH_TALEARR_M4B = 'settings/talearrM4b/fetchTalearrM4b';
export const SAVE_TALEARR_M4B = 'settings/talearrM4b/saveTalearrM4b';
export const SET_TALEARR_M4B_VALUE = 'settings/talearrM4b/setTalearrM4bValue';

//
// Action Creators

export const fetchTalearrM4b = createThunk(FETCH_TALEARR_M4B);
export const saveTalearrM4b = createThunk(SAVE_TALEARR_M4B);
export const setTalearrM4bValue = createAction(SET_TALEARR_M4B_VALUE, (payload) => {
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
    [FETCH_TALEARR_M4B]: createFetchHandler(section, '/config/talearrm4b'),
    [SAVE_TALEARR_M4B]: createSaveHandler(section, '/config/talearrm4b')
  },

  //
  // Reducers

  reducers: {
    [SET_TALEARR_M4B_VALUE]: createSetSettingValueReducer(section)
  }

};
