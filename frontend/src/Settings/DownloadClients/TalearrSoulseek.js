import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './TalearrSoulseek.css';

const LEGEND = 'Soulseek (slskd)';

const FIELDS = [
  'enabled',
  'url',
  'apiKey',
  'downloadsPath',
  'searchTimeoutSeconds',
  'requireFreeUploadSlot',
  'maxPeerQueueLength',
  'minUploadSpeedKbps',
  'maxActiveGrabsPerPeer',
  'maxActiveGrabs',
  'queueTimeoutHours',
  'cleanupAfterImport'
];

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  if (Array.isArray(body) && body.length) {
    return body.map((e) => e.errorMessage).join(' ');
  }

  return (body && body.message) || fallback;
}

function editable(settings) {
  return FIELDS.reduce((acc, name) => {
    acc[name] = settings[name];
    return acc;
  }, {});
}

// TaleArr: opt-in Soulseek through slskd (docs/TALEARR_SOULSEEK.md). Not a download client
// provider, so a rollback to Chaptarr deletes nothing. Stored as the talearr.soulseek.settings
// Config row through /api/v1/config/talearrsoulseek. Saves with its own button.
class TalearrSoulseek extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      error: null,
      saved: null,
      form: null,
      isSaving: false,
      saveError: null,
      isTesting: false,
      testResult: null
    };
  }

  componentDidMount() {
    const { request } = createAjaxRequest({ url: '/config/talearrsoulseek' });

    request.done((data) => {
      this.setState({ isFetching: false, saved: editable(data), form: editable(data) });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the Soulseek settings.') });
    });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: value }, testResult: null });
  };

  onTestPress = () => {
    this.setState({ isTesting: true, testResult: null });

    const { request } = createAjaxRequest({
      url: '/config/talearrsoulseek/test',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((testResult) => {
      this.setState({ isTesting: false, testResult });
    });

    request.fail((xhr) => {
      this.setState({
        isTesting: false,
        testResult: { success: false, message: readError(xhr, 'The test could not be run.'), warnings: [] }
      });
    });
  };

  onSavePress = () => {
    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: '/config/talearrsoulseek/1',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((data) => {
      this.setState({ isSaving: false, saved: editable(data), form: editable(data) });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save the Soulseek settings.') });
    });
  };

  //
  // Render

  renderNumber(name, label, helpText, unit) {
    return (
      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{label}</FormLabel>
        <FormInputGroup
          type={inputTypes.NUMBER}
          name={name}
          value={this.state.form[name]}
          unit={unit}
          helpText={helpText}
          onChange={this.onInputChange}
        />
      </FormGroup>
    );
  }

  render() {
    const {
      isFetching,
      error,
      saved,
      form,
      isSaving,
      saveError,
      isTesting,
      testResult
    } = this.state;

    if (isFetching) {
      return (
        <FieldSet legend={LEGEND}>
          <LoadingIndicator />
        </FieldSet>
      );
    }

    if (error) {
      return (
        <FieldSet legend={LEGEND}>
          <Alert kind={kinds.DANGER}>{error}</Alert>
        </FieldSet>
      );
    }

    const isChanged = JSON.stringify(form) !== JSON.stringify(saved);
    const testWarnings = (testResult && testResult.warnings) || [];

    return (
      <FieldSet legend={LEGEND}>
        <div className={styles.help}>
          {'Search Soulseek for a book from its page and download the whole folder through your own slskd. Results are peer files, so they go through the same matching, language and bundle rules as indexer results, and finished downloads are imported for that book. Only search and download what you are allowed to.'}
        </div>

        <Form>
          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Enable Soulseek'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="enabled"
              value={form.enabled}
              helpText="Off by default. Adds Search Soulseek to the book page; nothing is searched automatically."
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'slskd URL'}</FormLabel>
            <FormInputGroup
              type={inputTypes.TEXT}
              name="url"
              value={form.url}
              helpText="For example http://slskd:5030"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'API key'}</FormLabel>
            <FormInputGroup
              type={inputTypes.PASSWORD}
              name="apiKey"
              value={form.apiKey}
              helpText="One of slskd's web.authentication.api_keys"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Downloads folder'}</FormLabel>
            <FormInputGroup
              type={inputTypes.PATH}
              name="downloadsPath"
              value={form.downloadsPath}
              helpText="slskd's downloads folder as TaleArr sees it, so map it into the TaleArr container too"
              onChange={this.onInputChange}
            />
          </FormGroup>

          {this.renderNumber('searchTimeoutSeconds', 'Search time', 'How long slskd collects answers', 'seconds')}

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Free upload slot only'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="requireFreeUploadSlot"
              value={form.requireFreeUploadSlot}
              helpText="Only offer peers that can send right now instead of queueing you behind others"
              onChange={this.onInputChange}
            />
          </FormGroup>

          {this.renderNumber('maxPeerQueueLength', 'Maximum peer queue', 'Skip peers with more queued uploads than this (0 = no limit)')}
          {this.renderNumber('minUploadSpeedKbps', 'Minimum peer speed', 'Skip slower peers (0 = no limit)', 'KiB/s')}
          {this.renderNumber('maxActiveGrabsPerPeer', 'Downloads per peer', 'Unfinished downloads from one peer at a time')}
          {this.renderNumber('maxActiveGrabs', 'Downloads in total', 'Unfinished Soulseek downloads at a time')}
          {this.renderNumber('queueTimeoutHours', 'Give up after', 'A download the peer has not started by then is cancelled', 'hours')}

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Clean up after import'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="cleanupAfterImport"
              value={form.cleanupAfterImport}
              helpText="Clear finished transfers in slskd and remove the leftover download folder"
              onChange={this.onInputChange}
            />
          </FormGroup>
        </Form>

        {
          testResult ?
            <Alert kind={testResult.success ? kinds.SUCCESS : kinds.DANGER}>
              {testResult.message}
            </Alert> :
            null
        }

        {
          testWarnings.map((w) => {
            return (
              <Alert
                key={w}
                kind={kinds.WARNING}
              >
                {w}
              </Alert>
            );
          })
        }

        {saveError ? <Alert kind={kinds.DANGER}>{saveError}</Alert> : null}

        <div className={styles.actions}>
          <SpinnerButton
            isSpinning={isTesting}
            isDisabled={!form.url}
            onPress={this.onTestPress}
          >
            {'Test'}
          </SpinnerButton>

          <SpinnerButton
            kind={kinds.PRIMARY}
            isSpinning={isSaving}
            isDisabled={!isChanged}
            onPress={this.onSavePress}
          >
            {'Save Soulseek'}
          </SpinnerButton>
          {isChanged ? null : <span className={styles.saved}>{'Saved'}</span>}
        </div>
      </FieldSet>
    );
  }
}

export default TalearrSoulseek;
