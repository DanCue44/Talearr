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
import styles from './TalearrQualityLadder.css';

const LEGEND = 'Audio quality ladder and ebook formats';

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  if (Array.isArray(body) && body.length) {
    return body.map((e) => e.errorMessage).join(' ');
  }

  return (body && body.message) || fallback;
}

function toForm(saved) {
  return {
    ...saved,
    ebookFormats: (saved.ebookFormats || []).join(', ')
  };
}

// TaleArr: audio quality ladder and ebook format preference on Settings > Profiles. Stored as
// talearr.ladder.* rows in the Config table through /api/v1/config/talearrqualityladder.
class TalearrQualityLadder extends Component {

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
    const { request } = createAjaxRequest({ url: '/config/talearrqualityladder' });

    request.done((saved) => {
      this.setState({ isFetching: false, saved: toForm(saved), form: toForm(saved) });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the quality ladder settings.') });
    });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: name === 'minimumAudioKbps' ? parseInt(value) || 0 : value } });
  };

  onSavePress = () => {
    const { form } = this.state;
    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: '/config/talearrqualityladder/1',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        id: 1,
        audioRanking: form.audioRanking,
        audioUpgrades: form.audioUpgrades,
        minimumAudioKbps: form.minimumAudioKbps,
        ebookFormats: form.ebookFormats.split(/[\s,;>]+/).filter((f) => f),
        ebookUpgrades: form.ebookUpgrades
      })
    });

    request.done((saved) => {
      this.setState({ isSaving: false, saved: toForm(saved), form: toForm(saved) });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, saveError: readError(xhr, 'Unable to save the quality ladder settings.') });
    });
  };

  //
  // Render

  render() {
    const { isFetching, error, saved, form, isSaving, saveError } = this.state;

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

    return (
      <FieldSet legend={LEGEND}>
        <div className={styles.help}>
          {'TaleArr ranks releases the quality profile ranks equal. Audio tiers: AAC at 64 kbps or more, MP3 at 128 kbps or more and lossless audio are good; under 32 kbps is very low. The bitrate is read from the release title or estimated from its size against the book\'s known runtime. The quality profile always decides first.'}
        </div>

        <Form>
          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Rank by audio quality'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="audioRanking"
              value={form.audioRanking}
              helpText="Prefer the better audio tier among releases of the same format, and rank very low bitrate or implausibly small audio releases below all others"
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Upgrade low bitrate audio'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="audioUpgrades"
              value={form.audioUpgrades}
              helpText="Replace audiobook files below the good tier with a clearly better copy of the same recording (same narrator and abridgement; the upgrade guard checks the runtime on import). Needs the quality profile to allow upgrades. Off by default."
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Minimum audio bitrate'}</FormLabel>
            <FormInputGroup
              type={inputTypes.NUMBER}
              name="minimumAudioKbps"
              value={form.minimumAudioKbps}
              min={0}
              max={128}
              unit="kbps"
              helpText="Reject audio releases whose known bitrate is lower, and implausibly small ones. 0 turns it off (they are only ranked last). Interactive search can still grab them."
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Ebook format order'}</FormLabel>
            <FormInputGroup
              type={inputTypes.TEXT}
              name="ebookFormats"
              value={form.ebookFormats}
              helpText={`Most preferred first, from ${(saved.knownEbookFormats || []).join(', ')}. Breaks ties the quality profile leaves (formats in one group).`}
              onChange={this.onInputChange}
            />
          </FormGroup>

          <FormGroup size={sizes.MEDIUM}>
            <FormLabel>{'Upgrade ebook format'}</FormLabel>
            <FormInputGroup
              type={inputTypes.CHECK}
              name="ebookUpgrades"
              value={form.ebookUpgrades}
              helpText="Replace an ebook with a more preferred format the quality profile ranks equal. Needs the quality profile to allow upgrades. Off by default (rank only)."
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
            {'Save quality ladder'}
          </SpinnerButton>
          {isChanged ? null : <span className={styles.saved}>{'Saved'}</span>}
        </div>
      </FieldSet>
    );
  }
}

export default TalearrQualityLadder;
