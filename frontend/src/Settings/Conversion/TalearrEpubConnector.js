import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchTalearrEpub, saveTalearrEpub, setTalearrEpubValue } from 'Store/Actions/settingsActions';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import TalearrEpub from './TalearrEpub';

// Talearr: Kindle to EPUB section on Settings > Conversion.
const SECTION = 'talearrEpub';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createSettingsSectionSelector(SECTION),
    createCommandExecutingSelector(commandNames.TALEARR_CONVERT_LIBRARY_TO_EPUB),
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
  dispatchFetchTalearrEpub: fetchTalearrEpub,
  dispatchSetTalearrEpubValue: setTalearrEpubValue,
  dispatchSaveTalearrEpub: saveTalearrEpub,
  dispatchClearPendingChanges: clearPendingChanges,
  dispatchExecuteCommand: executeCommand
};

class TalearrEpubConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      dispatchFetchTalearrEpub,
      dispatchSaveTalearrEpub,
      onChildMounted
    } = this.props;

    dispatchFetchTalearrEpub();
    onChildMounted(dispatchSaveTalearrEpub);
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
    this.props.dispatchClearPendingChanges({ section: 'settings.talearrEpub' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetTalearrEpubValue({ name, value });
  };

  // Talearr: convert every Kindle ebook already in the library that has no EPUB (manual only).
  onConvertLibraryPress = () => {
    this.props.dispatchExecuteCommand({ name: commandNames.TALEARR_CONVERT_LIBRARY_TO_EPUB });
  };

  //
  // Render

  render() {
    return (
      <TalearrEpub
        onInputChange={this.onInputChange}
        onConvertLibraryPress={this.onConvertLibraryPress}
        {...this.props}
      />
    );
  }
}

TalearrEpubConnector.propTypes = {
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  dispatchFetchTalearrEpub: PropTypes.func.isRequired,
  dispatchSetTalearrEpubValue: PropTypes.func.isRequired,
  dispatchSaveTalearrEpub: PropTypes.func.isRequired,
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  dispatchExecuteCommand: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrEpubConnector);
