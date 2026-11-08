import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchTalearrCalibre, saveTalearrCalibre, setTalearrCalibreValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import TalearrCalibre from './TalearrCalibre';

// Talearr: Calibre library section on Settings > Metadata.
const SECTION = 'talearrCalibre';

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
  dispatchFetchTalearrCalibre: fetchTalearrCalibre,
  dispatchSetTalearrCalibreValue: setTalearrCalibreValue,
  dispatchSaveTalearrCalibre: saveTalearrCalibre,
  dispatchClearPendingChanges: clearPendingChanges
};

class TalearrCalibreConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      dispatchFetchTalearrCalibre,
      dispatchSaveTalearrCalibre,
      onChildMounted
    } = this.props;

    dispatchFetchTalearrCalibre();
    onChildMounted(dispatchSaveTalearrCalibre);
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
    this.props.dispatchClearPendingChanges({ section: 'settings.talearrCalibre' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetTalearrCalibreValue({ name, value });
  };

  //
  // Render

  render() {
    return (
      <TalearrCalibre
        onInputChange={this.onInputChange}
        {...this.props}
      />
    );
  }
}

TalearrCalibreConnector.propTypes = {
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  dispatchFetchTalearrCalibre: PropTypes.func.isRequired,
  dispatchSetTalearrCalibreValue: PropTypes.func.isRequired,
  dispatchSaveTalearrCalibre: PropTypes.func.isRequired,
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrCalibreConnector);
