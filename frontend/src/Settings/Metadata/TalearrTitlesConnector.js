import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchTalearrTitles, saveTalearrTitles, setTalearrTitlesValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import TalearrTitles from './TalearrTitles';

// Talearr: Book Titles section on Settings > Metadata.
const SECTION = 'talearrTitles';

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
  dispatchFetchTalearrTitles: fetchTalearrTitles,
  dispatchSetTalearrTitlesValue: setTalearrTitlesValue,
  dispatchSaveTalearrTitles: saveTalearrTitles,
  dispatchClearPendingChanges: clearPendingChanges
};

class TalearrTitlesConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      dispatchFetchTalearrTitles,
      dispatchSaveTalearrTitles,
      onChildMounted
    } = this.props;

    dispatchFetchTalearrTitles();
    onChildMounted(dispatchSaveTalearrTitles);
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
    this.props.dispatchClearPendingChanges({ section: 'settings.talearrTitles' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetTalearrTitlesValue({ name, value });
  };

  //
  // Render

  render() {
    return (
      <TalearrTitles
        onInputChange={this.onInputChange}
        {...this.props}
      />
    );
  }
}

TalearrTitlesConnector.propTypes = {
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  dispatchFetchTalearrTitles: PropTypes.func.isRequired,
  dispatchSetTalearrTitlesValue: PropTypes.func.isRequired,
  dispatchSaveTalearrTitles: PropTypes.func.isRequired,
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrTitlesConnector);
