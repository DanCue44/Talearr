import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import {
  fetchConversionSettings,
  saveConversionSettings,
  setConversionSettingsValue
} from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import ConversionSettings from './ConversionSettings';

const SECTION = 'conversion';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.advancedSettings,
    createSettingsSectionSelector(SECTION),
    (advancedSettings, sectionSettings) => {
      return {
        advancedSettings,
        ...sectionSettings,
        hasPendingChanges: sectionSettings.hasPendingChanges
      };
    }
  );
}

const mapDispatchToProps = {
  fetchConversionSettings,
  setConversionSettingsValue,
  saveConversionSettings,
  clearPendingChanges
};

class ConversionSettingsConnector extends Component {

  constructor(props, context) {
    super(props, context);

    // Talearr: the M4B merge section saves with the same toolbar button.
    this._talearrSaveCallback = null;

    this.state = {
      talearrIsSaving: false,
      talearrHasPendingChanges: false
    };
  }

  componentDidMount() {
    this.props.fetchConversionSettings();
  }

  componentWillUnmount() {
    this.props.clearPendingChanges({ section: `settings.${SECTION}` });
  }

  onInputChange = ({ name, value }) => {
    this.props.setConversionSettingsValue({ name, value });
  };

  onTalearrChildMounted = (saveCallback) => {
    this._talearrSaveCallback = saveCallback;
  };

  onTalearrChildStateChange = ({ isSaving, hasPendingChanges }) => {
    this.setState({
      talearrIsSaving: isSaving,
      talearrHasPendingChanges: hasPendingChanges
    });
  };

  onSavePress = () => {
    if (this.props.hasPendingChanges) {
      this.props.saveConversionSettings();
    }

    if (this._talearrSaveCallback && this.state.talearrHasPendingChanges) {
      this._talearrSaveCallback();
    }
  };

  render() {
    const {
      talearrIsSaving,
      talearrHasPendingChanges
    } = this.state;

    return (
      <ConversionSettings
        onInputChange={this.onInputChange}
        {...this.props}
        isSaving={this.props.isSaving || talearrIsSaving}
        hasPendingChanges={this.props.hasPendingChanges || talearrHasPendingChanges}
        onSavePress={this.onSavePress}
        onTalearrChildMounted={this.onTalearrChildMounted}
        onTalearrChildStateChange={this.onTalearrChildStateChange}
      />
    );
  }
}

ConversionSettingsConnector.propTypes = {
  isSaving: PropTypes.bool,
  hasPendingChanges: PropTypes.bool,
  fetchConversionSettings: PropTypes.func.isRequired,
  setConversionSettingsValue: PropTypes.func.isRequired,
  saveConversionSettings: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(ConversionSettingsConnector);
