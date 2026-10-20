import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import SelectInput from 'Components/Form/SelectInput';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './TalearrFormatPolicy.css';

export const policyOptions = [
  { key: 'both', value: 'Both: grab every monitored format' },
  { key: 'preferAudio', value: 'Prefer audiobook, accept ebook, upgrade to audio later' },
  { key: 'audioOnly', value: 'Audiobook only' },
  { key: 'ebookOnly', value: 'Ebook only' }
];

function createMapStateToProps() {
  return createSelector(
    (state) => state.authors.items,
    (authors) => {
      return {
        authors: (authors || [])
          .map((a) => ({ id: a.id, name: a.authorName || a.name }))
          .sort((a, b) => (a.name || '').localeCompare(b.name || ''))
      };
    }
  );
}

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  if (Array.isArray(body) && body.length) {
    return body.map((e) => e.errorMessage).join(' ');
  }

  return (body && body.message) || fallback;
}

// Talearr: format policy on Settings > Indexers (docs/TALEARR_FORMAT_POLICY.md). Stored as
// talearr.formatpolicy.* rows in the Config table through /api/v1/config/talearrformatpolicy.
class TalearrFormatPolicy extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      error: null,
      saved: null,
      form: null,
      newAuthorId: '',
      isSaving: false,
      saveError: null
    };
  }

  componentDidMount() {
    const { request } = createAjaxRequest({ url: '/config/talearrformatpolicy' });

    request.done((saved) => {
      this.setState({ isFetching: false, saved, form: saved });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the format policy.') });
    });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: name === 'fallbackHours' ? parseInt(value) || 0 : value } });
  };

  onAuthorPolicyChange = (authorId, value) => {
    const authors = this.state.form.authors.map((a) => (a.authorId === authorId ? { ...a, policy: value } : a));
    this.setState({ form: { ...this.state.form, authors } });
  };

  onRemoveAuthorPress = (authorId) => {
    this.setState({ form: { ...this.state.form, authors: this.state.form.authors.filter((a) => a.authorId !== authorId) } });
  };

  onNewAuthorChange = ({ value }) => {
    this.setState({ newAuthorId: value });
  };

  onAddAuthorPress = () => {
    const id = parseInt(this.state.newAuthorId);
    const author = this.props.authors.find((a) => a.id === id);

    if (!author) {
      return;
    }

    const authors = [...this.state.form.authors, { authorId: id, authorName: author.name, policy: 'preferAudio' }];
    this.setState({ form: { ...this.state.form, authors }, newAuthorId: '' });
  };

  onSavePress = () => {
    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: '/config/talearrformatpolicy/1',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.form, id: 1 })
    });

    request.done((saved) => {
      this.setState({ isSaving: false, saved, form: saved });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save the format policy.') });
    });
  };

  //
  // Render

  render() {
    const { authors } = this.props;
    const { isFetching, error, saved, form, newAuthorId, isSaving, saveError } = this.state;

    if (isFetching) {
      return (
        <FieldSet legend="Format policy">
          <LoadingIndicator />
        </FieldSet>
      );
    }

    if (error) {
      return (
        <FieldSet legend="Format policy">
          <Alert kind={kinds.DANGER}>{error}</Alert>
        </FieldSet>
      );
    }

    const isChanged = JSON.stringify(form) !== JSON.stringify(saved);
    const used = new Set(form.authors.map((a) => a.authorId));
    const authorOptions = [{ key: '', value: 'Choose an author', isDisabled: true }]
      .concat(authors.filter((a) => !used.has(a.id)).map((a) => ({ key: String(a.id), value: a.name })));

    return (
      <FieldSet legend="Format policy">
        <div className={styles.help}>
          {'Which format Talearr grabs for a monitored book. Monitoring still decides which books are wanted in which format; the policy filters the grabs. Interactive search shows a skipped release as "Format policy" and can still grab it.'}
        </div>

        <Form>
          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Default'}</FormLabel>
            <FormInputGroup
              type={inputTypes.SELECT}
              name="default"
              values={policyOptions}
              value={form.default}
              helpText="Prefer audiobook: monitor both formats; the ebook is only grabbed once the audiobook has been searched and missing for the fallback time, and the audiobook is still grabbed when it appears later (the ebook is kept)."
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Fallback after'}</FormLabel>
            <FormInputGroup
              type={inputTypes.NUMBER}
              name="fallbackHours"
              value={form.fallbackHours}
              min={0}
              max={2160}
              unit="hours"
              helpText="Prefer audiobook: how long the audiobook may be missing before the ebook is accepted"
              onChange={this.onInputChange}
            />
          </FormGroup>
        </Form>

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Per author'}</FormLabel>

          <div className={styles.overrides}>

            {
              form.authors.map((a) => {
                return (
                  <div
                    key={a.authorId}
                    className={styles.overrideRow}
                  >
                    <span className={styles.authorName}>{a.authorName || `Author ${a.authorId}`}</span>
                    <div className={styles.select}>
                      <SelectInput
                        name={`author-${a.authorId}`}
                        values={policyOptions}
                        value={a.policy}
                        onChange={({ value }) => this.onAuthorPolicyChange(a.authorId, value)}
                      />
                    </div>
                    <Button
                      size={sizes.SMALL}
                      onPress={() => this.onRemoveAuthorPress(a.authorId)}
                    >
                      {'Use default'}
                    </Button>
                  </div>
                );
              })
            }

            <div className={styles.addRow}>
              <div className={styles.select}>
                <SelectInput
                  name="newAuthor"
                  values={authorOptions}
                  value={newAuthorId}
                  onChange={this.onNewAuthorChange}
                />
              </div>
              <Button
                isDisabled={!newAuthorId}
                onPress={this.onAddAuthorPress}
              >
                {'Add author'}
              </Button>
            </div>
          </div>
        </FormGroup>

        {saveError ? <Alert kind={kinds.DANGER}>{saveError}</Alert> : null}

        <div className={styles.actions}>
          <SpinnerButton
            kind={kinds.PRIMARY}
            isSpinning={isSaving}
            isDisabled={!isChanged}
            onPress={this.onSavePress}
          >
            {'Save format policy'}
          </SpinnerButton>
          {isChanged ? null : <span className={styles.saved}>{'Saved'}</span>}
        </div>
      </FieldSet>
    );
  }
}

TalearrFormatPolicy.propTypes = {
  authors: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default connect(createMapStateToProps)(TalearrFormatPolicy);
