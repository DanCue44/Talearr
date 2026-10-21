import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FormInputGroup from 'Components/Form/FormInputGroup';
import SpinnerButton from 'Components/Link/SpinnerButton';
import { inputTypes, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { ageRatingOptions, readError } from './comicsFormat';
import styles from './Comics.css';

// Talearr: set a series' age rating in place, wherever a missing or wrong rating gets in the way.
class AgeRatingFix extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      value: '',
      isSaving: false,
      error: null
    };
  }

  //
  // Listeners

  onChange = ({ value }) => {
    this.setState({ value, error: null });
  };

  onSetPress = () => {
    const { seriesId, onSaved } = this.props;

    this.setState({ isSaving: true, error: null });

    const { request } = createAjaxRequest({
      url: `/talearr/comics/series/${seriesId}`,
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ageRatingOverride: this.state.value })
    });

    request.done((series) => {
      this.setState({ isSaving: false });
      onSaved(series);
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, error: readError(xhr, 'Unable to set the age rating.') });
    });
  };

  //
  // Render

  render() {
    const { ageRatings } = this.props;
    const { value, isSaving, error } = this.state;

    return (
      <span className={styles.ratingFix}>
        <FormInputGroup
          type={inputTypes.SELECT}
          name="ageRating"
          values={ageRatingOptions(ageRatings, 'Choose a rating')}
          value={value}
          onChange={this.onChange}
        />

        <SpinnerButton
          kind={kinds.PRIMARY}
          isSpinning={isSaving}
          isDisabled={!value}
          onPress={this.onSetPress}
        >
          {'Set age rating'}
        </SpinnerButton>

        {error ? <span className={styles.ratingError}>{error}</span> : null}
      </span>
    );
  }
}

AgeRatingFix.propTypes = {
  seriesId: PropTypes.number.isRequired,
  ageRatings: PropTypes.arrayOf(PropTypes.string).isRequired,
  onSaved: PropTypes.func.isRequired
};

export default AgeRatingFix;
