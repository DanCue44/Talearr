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
import formatDateTime from 'Utilities/Date/formatDateTime';
import styles from './TalearrFormatPolicy.css';

const scheduleOptions = [
  { key: 'off', value: 'Off' },
  { key: 'weekly', value: 'Weekly' },
  { key: 'daily', value: 'Daily' }
];

const numberFields = ['booksPerRun', 'staggerSeconds'];

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  if (Array.isArray(body) && body.length) {
    return body.map((e) => e.errorMessage).join(' ');
  }

  return (body && body.message) || fallback;
}

function editable(settings) {
  return {
    schedule: settings.schedule,
    booksPerRun: settings.booksPerRun,
    staggerSeconds: settings.staggerSeconds
  };
}

// TaleArr: scheduled missing-books search on Settings > Indexers. Stored as
// talearr.missingsearch.* rows in the Config table through /api/v1/config/talearrmissingsearch.
class TalearrMissingSearch extends Component {

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
      saveError: null
    };
  }

  componentDidMount() {
    const { request } = createAjaxRequest({ url: '/config/talearrmissingsearch' });

    request.done((saved) => {
      this.setState({ isFetching: false, saved, form: editable(saved) });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the scheduled missing search settings.') });
    });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    const parsed = numberFields.includes(name) ? parseInt(value) || 0 : value;
    this.setState({ form: { ...this.state.form, [name]: parsed } });
  };

  onSavePress = () => {
    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: '/config/talearrmissingsearch/1',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((saved) => {
      this.setState({ isSaving: false, saved, form: editable(saved) });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save the scheduled missing search settings.') });
    });
  };

  //
  // Render

  render() {
    const { isFetching, error, saved, form, isSaving, saveError } = this.state;
    const legend = 'Scheduled missing search';

    if (isFetching) {
      return (
        <FieldSet legend={legend}>
          <LoadingIndicator />
        </FieldSet>
      );
    }

    if (error) {
      return (
        <FieldSet legend={legend}>
          <Alert kind={kinds.DANGER}>{error}</Alert>
        </FieldSet>
      );
    }

    const isChanged = JSON.stringify(form) !== JSON.stringify(editable(saved));
    const isOff = form.schedule === 'off';
    const nextRun = saved.nextRun ? formatDateTime(saved.nextRun, 'YYYY-MM-DD', 'HH:mm') : null;

    return (
      <FieldSet legend={legend}>
        <div className={styles.help}>
          {'Books are searched when they are added; after that only RSS finds new uploads, so a book requested before an indexer was added may never be found. This searches monitored missing books again on a schedule, the ones searched longest ago first. It is an automatic search: delay profiles and every grab protection apply (Wanted > Search All Missing is a manual search and skips delay profiles). The task is listed in System > Tasks.'}
        </div>

        <Form>
          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Search for missing books on a schedule'}</FormLabel>
            <FormInputGroup
              type={inputTypes.SELECT}
              name="schedule"
              values={scheduleOptions}
              value={form.schedule}
              helpText={nextRun && !isChanged ? `Next run: ${nextRun}` : 'Off by default'}
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Books per run'}</FormLabel>
            <FormInputGroup
              type={inputTypes.NUMBER}
              name="booksPerRun"
              value={form.booksPerRun}
              min={1}
              max={200}
              isDisabled={isOff}
              helpText="How many missing books each run searches"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Wait between books'}</FormLabel>
            <FormInputGroup
              type={inputTypes.NUMBER}
              name="staggerSeconds"
              value={form.staggerSeconds}
              min={0}
              max={600}
              unit="seconds"
              isDisabled={isOff}
              helpText="Pause between two book searches so indexers are not hammered"
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
            {'Save scheduled search'}
          </SpinnerButton>
          {isChanged ? null : <span className={styles.saved}>{'Saved'}</span>}
        </div>
      </FieldSet>
    );
  }
}

export default TalearrMissingSearch;
