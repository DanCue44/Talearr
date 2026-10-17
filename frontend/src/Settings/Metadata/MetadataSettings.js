import React, { Component } from 'react';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import MetadatasConnector from './Metadata/MetadatasConnector';
import MetadataProviderConnector from './MetadataProvider/MetadataProviderConnector';
import TalearrMetadataFallbackConnector from './TalearrMetadataFallbackConnector';
import TalearrRecordingsConnector from './TalearrRecordingsConnector';

class MetadataSettings extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._saveCallback = null;

    // TaleArr: the recordings section saves with the same toolbar button.
    this._talearrSaveCallback = null;

    // TaleArr: the metadata fallback section saves with the same toolbar button.
    this._fallbackSaveCallback = null;

    this.state = {
      isSaving: false,
      hasPendingChanges: false,
      talearrIsSaving: false,
      talearrHasPendingChanges: false,
      fallbackIsSaving: false,
      fallbackHasPendingChanges: false
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

  onFallbackChildMounted = (saveCallback) => {
    this._fallbackSaveCallback = saveCallback;
  };

  onFallbackChildStateChange = ({ isSaving, hasPendingChanges }) => {
    this.setState({
      fallbackIsSaving: isSaving,
      fallbackHasPendingChanges: hasPendingChanges
    });
  };

  onSavePress = () => {
    if (this._saveCallback) {
      this._saveCallback();
    }

    if (this._talearrSaveCallback && this.state.talearrHasPendingChanges) {
      this._talearrSaveCallback();
    }

    if (this._fallbackSaveCallback && this.state.fallbackHasPendingChanges) {
      this._fallbackSaveCallback();
    }
  };

  //
  // Render
  render() {
    const {
      isSaving,
      hasPendingChanges,
      talearrIsSaving,
      talearrHasPendingChanges,
      fallbackIsSaving,
      fallbackHasPendingChanges
    } = this.state;

    return (
      <PageContent title={translate('MetadataSettings')}>
        <SettingsToolbarConnector
          isSaving={isSaving || talearrIsSaving || fallbackIsSaving}
          hasPendingChanges={hasPendingChanges || talearrHasPendingChanges || fallbackHasPendingChanges}
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
          <TalearrMetadataFallbackConnector
            onChildMounted={this.onFallbackChildMounted}
            onChildStateChange={this.onFallbackChildStateChange}
          />
          <MetadatasConnector />
        </PageContentBody>
      </PageContent>
    );
  }
}

export default MetadataSettings;
