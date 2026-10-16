import React, { Component } from 'react';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import MetadatasConnector from './Metadata/MetadatasConnector';
import MetadataProviderConnector from './MetadataProvider/MetadataProviderConnector';
import TalearrRecordingsConnector from './TalearrRecordingsConnector';

class MetadataSettings extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._saveCallback = null;

    // TaleArr: the recordings section saves with the same toolbar button.
    this._talearrSaveCallback = null;

    this.state = {
      isSaving: false,
      hasPendingChanges: false,
      talearrIsSaving: false,
      talearrHasPendingChanges: false
    };
  }

  //
  // Listeners

  onChildMounted = (saveCallback) => {
    this._saveCallback = saveCallback;
  };

  onChildStateChange = (payload) => {
    this.setState(payload);
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
    if (this._saveCallback) {
      this._saveCallback();
    }

    if (this._talearrSaveCallback && this.state.talearrHasPendingChanges) {
      this._talearrSaveCallback();
    }
  };

  //
  // Render
  render() {
    const {
      isSaving,
      hasPendingChanges,
      talearrIsSaving,
      talearrHasPendingChanges
    } = this.state;

    return (
      <PageContent title={translate('MetadataSettings')}>
        <SettingsToolbarConnector
          isSaving={isSaving || talearrIsSaving}
          hasPendingChanges={hasPendingChanges || talearrHasPendingChanges}
          onSavePress={this.onSavePress}
        />

        <PageContentBody>
          <MetadataProviderConnector
            onChildMounted={this.onChildMounted}
            onChildStateChange={this.onChildStateChange}
          />
          <TalearrRecordingsConnector
            onChildMounted={this.onTalearrChildMounted}
            onChildStateChange={this.onTalearrChildStateChange}
          />
          <MetadatasConnector />
        </PageContentBody>
      </PageContent>
    );
  }
}

export default MetadataSettings;
