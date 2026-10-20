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

const LEGEND = 'Reader hand-off';

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  if (Array.isArray(body) && body.length) {
    return body.map((e) => e.errorMessage).join(' ');
  }

  return (body && body.message) || fallback;
}

function editable(settings) {
  return {
    enabled: settings.enabled,
    path: settings.path || '',
    authorFolder: settings.authorFolder,
    convertToKepub: settings.convertToKepub
  };
}

// Talearr: copy imported ebooks to a watch folder such as Calibre-Web-Automated's ingest folder.
// Stored as talearr.readerhandoff.* rows in the Config table through
// /api/v1/config/talearrreaderhandoff (Chaptarr ignores them). Saves with its own button.
class TalearrReaderHandoff extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      error: null,
      saved: null,
      form: null,
      kepubifyInstalled: false,
      isSaving: false,
      saveError: null,
      isTesting: false,
      testResult: null
    };
  }

  componentDidMount() {
    const { request } = createAjaxRequest({ url: '/config/talearrreaderhandoff' });

    request.done((data) => {
      this.setState({
        isFetching: false,
        saved: editable(data),
        form: editable(data),
        kepubifyInstalled: data.kepubifyInstalled
      });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the reader hand-off settings.') });
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
      url: '/config/talearrreaderhandoff/test',
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
      url: '/config/talearrreaderhandoff/1',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((data) => {
      this.setState({
        isSaving: false,
        saved: editable(data),
        form: editable(data),
        kepubifyInstalled: data.kepubifyInstalled
      });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save the reader hand-off settings.') });
    });
  };

  //
  // Render

  render() {
    const {
      isFetching,
      error,
      saved,
      form,
      kepubifyInstalled,
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
          {'After an ebook from a download is imported, Talearr copies it to a watch folder, for example the ingest folder of Calibre-Web-Automated. The library file is never moved or changed, and audiobooks are skipped.'}
        </div>

        <Form>
          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Copy to watch folder'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="enabled"
              value={form.enabled}
              helpText="Off by default"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Watch folder'}</FormLabel>
            <FormInputGroup
              type={inputTypes.PATH}
              name="path"
              value={form.path}
              helpText="The folder as Talearr sees it, so map it into the Talearr container too"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Author subfolder'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="authorFolder"
              value={form.authorFolder}
              helpText="Put each copy in a subfolder named after the author"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Convert EPUB to KEPUB'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="convertToKepub"
              value={form.convertToKepub}
              helpText="For Kobo readers: the copy is converted with kepubify (must be installed in the container). The library EPUB stays as it is."
              helpTextWarning={form.convertToKepub && !kepubifyInstalled ? 'kepubify was not found on PATH, so EPUBs are copied unconverted' : undefined}
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
            isDisabled={!form.path}
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
            {'Save reader hand-off'}
          </SpinnerButton>
          {isChanged ? null : <span className={styles.saved}>{'Saved'}</span>}
        </div>
      </FieldSet>
    );
  }
}

export default TalearrReaderHandoff;
