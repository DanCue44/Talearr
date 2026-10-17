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
import styles from './TalearrReaderHandoff.css';

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  if (Array.isArray(body) && body.length) {
    return body.map((e) => e.errorMessage).join(' ');
  }

  return (body && body.message) || fallback;
}

function kindleForm(settings) {
  return {
    enabled: settings.enabled,
    autoSendOnImport: settings.autoSendOnImport,
    kindleAddresses: settings.kindleAddresses || [],
    emailConnectionId: settings.emailConnectionId || 0,
    server: settings.server || '',
    port: settings.port,
    requireEncryption: settings.requireEncryption,
    username: settings.username || '',
    password: settings.password || '',
    from: settings.from || ''
  };
}

// TaleArr: Send to Kindle. Stored as talearr.kindle.* Config rows through
// /api/v1/config/talearrkindle (Chaptarr ignores them). The password comes back masked.
class TalearrKindle extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      error: null,
      saved: null,
      form: null,
      connections: [],
      isSaving: false,
      saveError: null,
      isTesting: false,
      testResult: null
    };
  }

  componentDidMount() {
    const { request } = createAjaxRequest({ url: '/config/talearrkindle' });

    request.done((data) => {
      this.setState({ isFetching: false, saved: kindleForm(data), form: kindleForm(data) });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the Send to Kindle settings.') });
    });

    const connections = createAjaxRequest({ url: '/config/talearrkindle/emailconnections' }).request;
    connections.done((data) => {
      this.setState({ connections: data || [] });
    });
  }

  onInputChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: value }, testResult: null });
  };

  onConnectionChange = ({ name, value }) => {
    this.onInputChange({ name, value: parseInt(value) || 0 });
  };

  onTestPress = () => {
    this.setState({ isTesting: true, testResult: null });

    const { request } = createAjaxRequest({
      url: '/config/talearrkindle/test',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((testResult) => {
      this.setState({ isTesting: false, testResult });
    });

    request.fail((xhr) => {
      this.setState({ isTesting: false, testResult: { success: false, message: readError(xhr, 'The test could not be run.') } });
    });
  };

  onSavePress = () => {
    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: '/config/talearrkindle/1',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((data) => {
      this.setState({ isSaving: false, saved: kindleForm(data), form: kindleForm(data) });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save the Send to Kindle settings.') });
    });
  };

  render() {
    const {
      isFetching,
      error,
      saved,
      form,
      connections,
      isSaving,
      saveError,
      isTesting,
      testResult
    } = this.state;

    const legend = 'Send to Kindle';

    if (isFetching) {
      return <FieldSet legend={legend}><LoadingIndicator /></FieldSet>;
    }

    if (error) {
      return <FieldSet legend={legend}><Alert kind={kinds.DANGER}>{error}</Alert></FieldSet>;
    }

    const isChanged = JSON.stringify(form) !== JSON.stringify(saved);
    const ownSmtp = !form.emailConnectionId;
    const connectionOptions = [
      { key: 0, value: 'Use the SMTP settings below' },
      ...connections.map((c) => ({ key: c.id, value: `Email connection: ${c.name}` }))
    ];

    return (
      <FieldSet legend={legend}>
        <div className={styles.help}>
          {'Emails an ebook to your Send-to-Kindle address, from the Send to Kindle button on a book page or after each import. Amazon accepts EPUB, PDF, DOC(X), RTF, TXT and HTML up to 50 MB; MOBI and AZW3 are not accepted, so those books are skipped. Add the From address to the Approved Personal Document E-mail List in your Amazon account.'}
        </div>

        <Form>
          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Send to Kindle'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="enabled"
              value={form.enabled}
              helpText="Off by default"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Kindle addresses'}</FormLabel>
            <FormInputGroup
              type={inputTypes.TEXT_TAG}
              name="kindleAddresses"
              value={form.kindleAddresses}
              helpText="For example name_123@kindle.com. Find it under Manage Your Content and Devices > Preferences."
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Send after import'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="autoSendOnImport"
              value={form.autoSendOnImport}
              helpText="Also email each ebook imported from a download (not disk scans)"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Mail server'}</FormLabel>
            <FormInputGroup
              type={inputTypes.SELECT}
              name="emailConnectionId"
              value={form.emailConnectionId}
              values={connectionOptions}
              helpText="Reuse the server, login and From address of an Email connection (Settings > Connect)"
              onChange={this.onConnectionChange}
            />
          </FormGroup>

          {
            ownSmtp ?
              <>
                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{'SMTP server'}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="server"
                    value={form.server}
                    onChange={this.onInputChange}
                  />
                </FormGroup>

                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{'Port'}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.NUMBER}
                    name="port"
                    value={form.port}
                    onChange={this.onInputChange}
                  />
                </FormGroup>

                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{'Require encryption'}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="requireEncryption"
                    value={form.requireEncryption}
                    helpText="SSL on port 465, StartTLS on any other port"
                    onChange={this.onInputChange}
                  />
                </FormGroup>

                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{'Username'}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="username"
                    value={form.username}
                    onChange={this.onInputChange}
                  />
                </FormGroup>

                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{'Password'}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.PASSWORD}
                    name="password"
                    value={form.password}
                    onChange={this.onInputChange}
                  />
                </FormGroup>

                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{'From address'}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="from"
                    value={form.from}
                    helpText="Must be on your Approved Personal Document E-mail List"
                    onChange={this.onInputChange}
                  />
                </FormGroup>
              </> :
              null
          }
        </Form>

        {
          testResult ?
            <Alert kind={testResult.success ? kinds.SUCCESS : kinds.DANGER}>
              {testResult.message}
            </Alert> :
            null
        }

        {saveError ? <Alert kind={kinds.DANGER}>{saveError}</Alert> : null}

        <div className={styles.actions}>
          <SpinnerButton
            isSpinning={isTesting}
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
            {'Save Send to Kindle'}
          </SpinnerButton>
          {isChanged ? null : <span className={styles.saved}>{'Saved'}</span>}
        </div>
      </FieldSet>
    );
  }
}

export default TalearrKindle;
