import PropTypes from 'prop-types';
import React, { Component, Fragment } from 'react';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import { icons } from 'Helpers/Props';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import IndexersConnector from './Indexers/IndexersConnector';
import ManageIndexersModal from './Indexers/Manage/ManageIndexersModal';
import IndexerOptionsConnector from './Options/IndexerOptionsConnector';
import TalearrGrabPolicyConnector from './Options/TalearrGrabPolicyConnector';

class IndexerSettings extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._saveCallback = null;
    // TaleArr: the grab policy section saves with the same toolbar button.
    this._talearrSaveCallback = null;

    this.state = {
      isSaving: false,
      hasPendingChanges: false,
      talearrIsSaving: false,
      talearrHasPendingChanges: false,
      isManageIndexersOpen: false
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

  onManageIndexersPress = () => {
    this.setState({ isManageIndexersOpen: true });
  };

  onManageIndexersModalClose = () => {
    this.setState({ isManageIndexersOpen: false });
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
      isTestingAll,
      dispatchTestAllIndexers
    } = this.props;

    const {
      isSaving,
      hasPendingChanges,
      talearrIsSaving,
      talearrHasPendingChanges,
      isManageIndexersOpen
    } = this.state;

    return (
      <PageContent title={translate('IndexerSettings')}>
        <SettingsToolbarConnector
          isSaving={isSaving || talearrIsSaving}
          hasPendingChanges={hasPendingChanges || talearrHasPendingChanges}
          additionalButtons={
            <Fragment>
              <PageToolbarSeparator />

              <PageToolbarButton
                label={translate('TestAllIndexers')}
                iconName={icons.TEST}
                isSpinning={isTestingAll}
                onPress={dispatchTestAllIndexers}
              />

              <PageToolbarButton
                label={translate('ManageIndexers')}
                iconName={icons.MANAGE}
                onPress={this.onManageIndexersPress}
              />
            </Fragment>
          }
          onSavePress={this.onSavePress}
        />

        <PageContentBody>
          <IndexersConnector />

          <IndexerOptionsConnector
            onChildMounted={this.onChildMounted}
            onChildStateChange={this.onChildStateChange}
          />

          <TalearrGrabPolicyConnector
            onChildMounted={this.onTalearrChildMounted}
            onChildStateChange={this.onTalearrChildStateChange}
          />

          <ManageIndexersModal
            isOpen={isManageIndexersOpen}
            onModalClose={this.onManageIndexersModalClose}
          />
        </PageContentBody>
      </PageContent>
    );
  }
}

IndexerSettings.propTypes = {
  isTestingAll: PropTypes.bool.isRequired,
  dispatchTestAllIndexers: PropTypes.func.isRequired
};

export default IndexerSettings;
