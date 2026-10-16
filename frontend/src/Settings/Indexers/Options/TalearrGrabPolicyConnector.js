import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchTalearrGrabPolicy, saveTalearrGrabPolicy, setTalearrGrabPolicyValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import TalearrGrabPolicy from './TalearrGrabPolicy';

// TaleArr: grab policy section on Settings > Indexers.
const SECTION = 'talearrGrabPolicy';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createSettingsSectionSelector(SECTION),
    (advancedSettings, sectionSettings) => {
      return {
        advancedSettings,
        ...sectionSettings
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchTalearrGrabPolicy: fetchTalearrGrabPolicy,
  dispatchSetTalearrGrabPolicyValue: setTalearrGrabPolicyValue,
  dispatchSaveTalearrGrabPolicy: saveTalearrGrabPolicy,
  dispatchClearPendingChanges: clearPendingChanges
};

class TalearrGrabPolicyConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      dispatchFetchTalearrGrabPolicy,
      dispatchSaveTalearrGrabPolicy,
      onChildMounted
    } = this.props;

    dispatchFetchTalearrGrabPolicy();
    onChildMounted(dispatchSaveTalearrGrabPolicy);
  }

  componentDidUpdate(prevProps) {
    const {
      hasPendingChanges,
      isSaving,
      onChildStateChange
    } = this.props;

    if (
      prevProps.isSaving !== isSaving ||
      prevProps.hasPendingChanges !== hasPendingChanges
    ) {
      onChildStateChange({
        isSaving,
        hasPendingChanges
      });
    }
  }

  componentWillUnmount() {
    this.props.dispatchClearPendingChanges({ section: 'settings.talearrGrabPolicy' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetTalearrGrabPolicyValue({ name, value });
  };

  //
  // Render

  render() {
    return (
      <TalearrGrabPolicy
        onInputChange={this.onInputChange}
        {...this.props}
      />
    );
  }
}

TalearrGrabPolicyConnector.propTypes = {
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  dispatchFetchTalearrGrabPolicy: PropTypes.func.isRequired,
  dispatchSetTalearrGrabPolicyValue: PropTypes.func.isRequired,
  dispatchSaveTalearrGrabPolicy: PropTypes.func.isRequired,
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrGrabPolicyConnector);
