import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchTalearrM4b, saveTalearrM4b, setTalearrM4bValue } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import TalearrM4b from './TalearrM4b';

// Talearr: M4B merge section on Settings > Conversion.
const SECTION = 'talearrM4b';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createSettingsSectionSelector(SECTION),
    createCommandExecutingSelector(commandNames.TALEARR_CONVERT_LIBRARY_TO_M4B),
    (advancedSettings, sectionSettings, isConvertingLibrary) => {
      return {
        advancedSettings,
        ...sectionSettings,
        isConvertingLibrary
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchTalearrM4b: fetchTalearrM4b,
  dispatchSetTalearrM4bValue: setTalearrM4bValue,
  dispatchSaveTalearrM4b: saveTalearrM4b,
  dispatchClearPendingChanges: clearPendingChanges,
  dispatchExecuteCommand: executeCommand
};

class TalearrM4bConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      dispatchFetchTalearrM4b,
      dispatchSaveTalearrM4b,
      onChildMounted
    } = this.props;

    dispatchFetchTalearrM4b();
    onChildMounted(dispatchSaveTalearrM4b);
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
    this.props.dispatchClearPendingChanges({ section: 'settings.talearrM4b' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetTalearrM4bValue({ name, value });
  };

  // Talearr: merge every multi-file audiobook already in the library (manual only).
  onConvertLibraryPress = () => {
    this.props.dispatchExecuteCommand({ name: commandNames.TALEARR_CONVERT_LIBRARY_TO_M4B });
  };

  //
  // Render

  render() {
    return (
      <TalearrM4b
        onInputChange={this.onInputChange}
        onConvertLibraryPress={this.onConvertLibraryPress}
        {...this.props}
      />
    );
  }
}

TalearrM4bConnector.propTypes = {
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  dispatchFetchTalearrM4b: PropTypes.func.isRequired,
  dispatchSetTalearrM4bValue: PropTypes.func.isRequired,
  dispatchSaveTalearrM4b: PropTypes.func.isRequired,
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  dispatchExecuteCommand: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrM4bConnector);
