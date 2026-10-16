import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

// TaleArr: recordings settings (Audible marketplace and provider, search on add, keep both).

//
// Variables

const section = 'settings.talearrRecordings';

//
// Actions Types

export const FETCH_TALEARR_RECORDINGS = 'settings/talearrRecordings/fetchTalearrRecordings';
export const SAVE_TALEARR_RECORDINGS = 'settings/talearrRecordings/saveTalearrRecordings';
export const SET_TALEARR_RECORDINGS_VALUE = 'settings/talearrRecordings/setTalearrRecordingsValue';

//
// Action Creators

export const fetchTalearrRecordings = createThunk(FETCH_TALEARR_RECORDINGS);
export const saveTalearrRecordings = createThunk(SAVE_TALEARR_RECORDINGS);
export const setTalearrRecordingsValue = createAction(SET_TALEARR_RECORDINGS_VALUE, (payload) => {
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
    [FETCH_TALEARR_RECORDINGS]: createFetchHandler(section, '/config/talearrrecordings'),
    [SAVE_TALEARR_RECORDINGS]: createSaveHandler(section, '/config/talearrrecordings')
  },

  //
  // Reducers

  reducers: {
    [SET_TALEARR_RECORDINGS_VALUE]: createSetSettingValueReducer(section)
  }

};
