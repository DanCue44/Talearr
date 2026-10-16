import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchTalearrRecordings, saveTalearrRecordings, setTalearrRecordingsValue } from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import TalearrRecordings from './TalearrRecordings';

// TaleArr: recordings section on Settings > Metadata.
const SECTION = 'talearrRecordings';

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
  dispatchFetchTalearrRecordings: fetchTalearrRecordings,
  dispatchSetTalearrRecordingsValue: setTalearrRecordingsValue,
  dispatchSaveTalearrRecordings: saveTalearrRecordings,
  dispatchClearPendingChanges: clearPendingChanges
};

class TalearrRecordingsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      dispatchFetchTalearrRecordings,
      dispatchSaveTalearrRecordings,
      onChildMounted
    } = this.props;

    dispatchFetchTalearrRecordings();
    onChildMounted(dispatchSaveTalearrRecordings);
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
    this.props.dispatchClearPendingChanges({ section: 'settings.talearrRecordings' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.dispatchSetTalearrRecordingsValue({ name, value });
  };

  //
  // Render

  render() {
    return (
      <TalearrRecordings
        onInputChange={this.onInputChange}
        {...this.props}
      />
    );
  }
}

TalearrRecordingsConnector.propTypes = {
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  dispatchFetchTalearrRecordings: PropTypes.func.isRequired,
  dispatchSetTalearrRecordingsValue: PropTypes.func.isRequired,
  dispatchSaveTalearrRecordings: PropTypes.func.isRequired,
  dispatchClearPendingChanges: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(TalearrRecordingsConnector);
