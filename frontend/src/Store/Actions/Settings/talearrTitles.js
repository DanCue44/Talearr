import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

// Talearr: Book Titles on Settings > Metadata (Clean Series From Titles).

//
// Variables

const section = 'settings.talearrTitles';

//
// Actions Types

export const FETCH_TALEARR_TITLES = 'settings/talearrTitles/fetchTalearrTitles';
export const SAVE_TALEARR_TITLES = 'settings/talearrTitles/saveTalearrTitles';
export const SET_TALEARR_TITLES_VALUE = 'settings/talearrTitles/setTalearrTitlesValue';

//
// Action Creators

export const fetchTalearrTitles = createThunk(FETCH_TALEARR_TITLES);
export const saveTalearrTitles = createThunk(SAVE_TALEARR_TITLES);
export const setTalearrTitlesValue = createAction(SET_TALEARR_TITLES_VALUE, (payload) => {
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
    [FETCH_TALEARR_TITLES]: createFetchHandler(section, '/config/talearrcalibre'),
    [SAVE_TALEARR_TITLES]: createSaveHandler(section, '/config/talearrcalibre')
  },

  //
  // Reducers

  reducers: {
    [SET_TALEARR_TITLES_VALUE]: createSetSettingValueReducer(section)
  }

};
