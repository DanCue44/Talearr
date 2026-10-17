import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import ClipboardButton from 'Components/Link/ClipboardButton';
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

function opdsForm(settings) {
  return {
    enabled: settings.enabled,
    pageSize: settings.pageSize,
    offerKepub: settings.offerKepub
  };
}

// TaleArr: OPDS catalog. Stored as talearr.opds.* Config rows through
// /api/v1/config/talearropds (Chaptarr ignores them). Saves with its own button.
class TalearrOpds extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      error: null,
      saved: null,
      form: null,
      feedPath: '/opds',
      kepubifyInstalled: false,
      isSaving: false,
      saveError: null
    };
  }

  componentDidMount() {
    const { request } = createAjaxRequest({ url: '/config/talearropds' });

    request.done((data) => {
      this.setState({
        isFetching: false,
        saved: opdsForm(data),
        form: opdsForm(data),
        feedPath: data.feedPath,
        kepubifyInstalled: data.kepubifyInstalled
      });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the OPDS settings.') });
    });
  }

  onInputChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: value } });
  };

  onSavePress = () => {
    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: '/config/talearropds/1',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((data) => {
      this.setState({
        isSaving: false,
        saved: opdsForm(data),
        form: opdsForm(data),
        feedPath: data.feedPath,
        kepubifyInstalled: data.kepubifyInstalled
      });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save the OPDS settings.') });
    });
  };

  render() {
    const {
      isFetching,
      error,
      saved,
      form,
      feedPath,
      kepubifyInstalled,
      isSaving,
      saveError
    } = this.state;

    const legend = 'OPDS catalog';

    if (isFetching) {
      return <FieldSet legend={legend}><LoadingIndicator /></FieldSet>;
    }

    if (error) {
      return <FieldSet legend={legend}><Alert kind={kinds.DANGER}>{error}</Alert></FieldSet>;
    }

    const isChanged = JSON.stringify(form) !== JSON.stringify(saved);
    const feedUrl = `${window.location.origin}${feedPath}`;
    const apiKey = (window.Chaptarr && window.Chaptarr.apiKey) || '';
    const keyUrl = `${feedUrl}?apikey=${encodeURIComponent(apiKey)}`;

    return (
      <FieldSet legend={legend}>
        <div className={styles.help}>
          {'A read-only OPDS feed of the ebooks in the library (authors, series, recently added and search) for readers such as KOReader, Panels, Thorium and Moon+ Reader. Sign in with HTTP Basic using your TaleArr login, or any user name with the API key as the password. Readers without Basic support can use the URL with the API key. Kobo: use KOReader with this feed, or the reader hand-off below with Calibre-Web-Automated.'}
        </div>

        <Form>
          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Serve OPDS feed'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="enabled"
              value={form.enabled}
              helpText="Off by default. While off, the feed answers 404."
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Feed URL'}</FormLabel>
            <FormInputGroup
              type={inputTypes.TEXT}
              name="feedUrl"
              value={feedUrl}
              readOnly={true}
              helpText={saved.enabled ? 'Add this to your reader as an OPDS catalog' : 'Turn the feed on and save first'}
              buttons={[
                <ClipboardButton
                  key="copy"
                  value={feedUrl}
                  kind={kinds.DEFAULT}
                />
              ]}
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'URL with API key'}</FormLabel>
            <FormInputGroup
              type={inputTypes.PASSWORD}
              name="keyUrl"
              value={keyUrl}
              readOnly={true}
              helpTextWarning="Anyone with this URL can read the feed. Prefer the Basic sign-in when the reader supports it."
              buttons={[
                <ClipboardButton
                  key="copy"
                  value={keyUrl}
                  kind={kinds.DEFAULT}
                />
              ]}
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Entries per page'}</FormLabel>
            <FormInputGroup
              type={inputTypes.NUMBER}
              name="pageSize"
              value={form.pageSize}
              min={10}
              max={200}
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Offer KEPUB downloads'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="offerKepub"
              value={form.offerKepub}
              helpText="Adds a KEPUB link next to each EPUB, converted on download with kepubify. The library file is never changed."
              helpTextWarning={form.offerKepub && !kepubifyInstalled ? 'kepubify was not found on PATH, so the plain EPUB is sent instead' : undefined}
              onChange={this.onInputChange}
            />
          </FormGroup>
        </Form>

        {saveError ? <Alert kind={kinds.DANGER}>{saveError}</Alert> : null}

        <div className={styles.actions}>
          <SpinnerButton
            kind={kinds.PRIMARY}
            isSpinning={isSaving}
            isDisabled={!isChanged}
            onPress={this.onSavePress}
          >
            {'Save OPDS'}
          </SpinnerButton>
          {isChanged ? null : <span className={styles.saved}>{'Saved'}</span>}
        </div>
      </FieldSet>
    );
  }
}

export default TalearrOpds;
