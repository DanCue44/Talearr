import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchTalearrMetadataFallback, saveTalearrMetadataFallback, setTalearrMetadataFallbackValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import TalearrMetadataFallback from './TalearrMetadataFallback';

// Talearr: metadata fallback section on Settings > Metadata.
const SECTION = 'talearrMetadataFallback';

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
  dispatchFetchTalearrMetadataFallback: fetchTalearrMetadataFallback,
  dispatchSetTalearrMetadataFallbackValue: setTalearrMetadataFallbackValue,
  dispatchSaveTalearrMetadataFallback: saveTalearrMetadataFallback,
  dispatchClearPendingChanges: clearPendingChanges
};

class TalearrMetadataFallbackConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      dispatchFetchTalearrMetadataFallback,
      dispatchSaveTalearrMetadataFallback,
      onChildMounted
    } = this.props;

    dispatchFetchTalearrMetadataFallback();
    onChildMounted(dispatchSaveTalearrMetadataFallback);
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
    this.props.dispatchClearPendingChanges({ section: 'settings.talearrMetadataFallback' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetTalearrMetadataFallbackValue({ name, value });
  };

  //
  // Render

  render() {
    return (
      <TalearrMetadataFallback
        onInputChange={this.onInputChange}
        {...this.props}
      />
    );
  }
}

TalearrMetadataFallbackConnector.propTypes = {
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  dispatchFetchTalearrMetadataFallback: PropTypes.func.isRequired,
  dispatchSetTalearrMetadataFallbackValue: PropTypes.func.isRequired,
  dispatchSaveTalearrMetadataFallback: PropTypes.func.isRequired,
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrMetadataFallbackConnector);
