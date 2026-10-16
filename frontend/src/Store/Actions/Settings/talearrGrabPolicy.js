import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

// TaleArr: grab policy settings (abridged audiobooks, bundles, automatic retry, re-search after failed grabs).

//
// Variables

const section = 'settings.talearrGrabPolicy';

//
// Actions Types

export const FETCH_TALEARR_GRAB_POLICY = 'settings/talearrGrabPolicy/fetchTalearrGrabPolicy';
export const SAVE_TALEARR_GRAB_POLICY = 'settings/talearrGrabPolicy/saveTalearrGrabPolicy';
export const SET_TALEARR_GRAB_POLICY_VALUE = 'settings/talearrGrabPolicy/setTalearrGrabPolicyValue';

//
// Action Creators

export const fetchTalearrGrabPolicy = createThunk(FETCH_TALEARR_GRAB_POLICY);
export const saveTalearrGrabPolicy = createThunk(SAVE_TALEARR_GRAB_POLICY);
export const setTalearrGrabPolicyValue = createAction(SET_TALEARR_GRAB_POLICY_VALUE, (payload) => {
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
    [FETCH_TALEARR_GRAB_POLICY]: createFetchHandler(section, '/config/talearrgrabpolicy'),
    [SAVE_TALEARR_GRAB_POLICY]: createSaveHandler(section, '/config/talearrgrabpolicy')
  },

  //
  // Reducers

  reducers: {
    [SET_TALEARR_GRAB_POLICY_VALUE]: createSetSettingValueReducer(section)
  }

};
