import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

// Talearr: Open Library metadata fallback (setting and live status).

//
// Variables

const section = 'settings.talearrMetadataFallback';

//
// Actions Types

export const FETCH_TALEARR_METADATA_FALLBACK = 'settings/talearrMetadataFallback/fetchTalearrMetadataFallback';
export const SAVE_TALEARR_METADATA_FALLBACK = 'settings/talearrMetadataFallback/saveTalearrMetadataFallback';
export const SET_TALEARR_METADATA_FALLBACK_VALUE = 'settings/talearrMetadataFallback/setTalearrMetadataFallbackValue';

//
// Action Creators

export const fetchTalearrMetadataFallback = createThunk(FETCH_TALEARR_METADATA_FALLBACK);
export const saveTalearrMetadataFallback = createThunk(SAVE_TALEARR_METADATA_FALLBACK);
export const setTalearrMetadataFallbackValue = createAction(SET_TALEARR_METADATA_FALLBACK_VALUE, (payload) => {
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
    [FETCH_TALEARR_METADATA_FALLBACK]: createFetchHandler(section, '/config/talearrmetadatafallback'),
    [SAVE_TALEARR_METADATA_FALLBACK]: createSaveHandler(section, '/config/talearrmetadatafallback')
  },

  //
  // Reducers

  reducers: {
    [SET_TALEARR_METADATA_FALLBACK_VALUE]: createSetSettingValueReducer(section)
  }

};
