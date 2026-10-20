import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';

// Talearr: genre and content type rules for a metadata profile. Saved with the profile as
// talearrContentFilter (stored by Talearr as a talearr.* Config row, not in MetadataProfiles).

const NAME = 'talearrContentFilter';

const contentTypes = [
  {
    key: 'skipShortStories',
    label: 'Skip Short Stories',
    helpText: 'Skip books the metadata lists as short stories or short story collections'
  },
  {
    key: 'skipNovellas',
    label: 'Skip Novellas',
    helpText: 'Skip books listed as novellas. Minimum Pages above also skips short books by page count'
  },
  {
    key: 'skipAnthologies',
    label: 'Skip Anthologies',
    helpText: 'Skip anthologies and literary collections (by genre, or "Anthology" in the title)'
  },
  {
    key: 'skipGraphicNovels',
    label: 'Skip Graphic Novels',
    helpText: 'Skip comics, graphic novels and manga'
  }
];

class TalearrContentFilterFields extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      genres: []
    };
  }

  componentDidMount() {
    this._request = createAjaxRequest({
      url: '/talearr/genres',
      dataType: 'json'
    });

    this._request.request.done((data) => {
      this.setState({ genres: (data || []).slice(0, 15).map((g) => g.genre) });
    });
  }

  componentWillUnmount() {
    if (this._request) {
      this._request.abortRequest();
    }
  }

  //
  // Listeners

  onChange = ({ name, value }) => {
    this.props.onInputChange({
      name: NAME,
      value: {
        ...this.getValue(),
        [name]: value
      }
    });
  };

  //
  // Render

  getValue() {
    const field = this.props.field;

    return (field && field.value) || {};
  }

  render() {
    const value = this.getValue();
    const { genres } = this.state;
    const genreHint = genres.length ?
      `Most common genres in your library: ${genres.join(', ')}` :
      'Genres come from the metadata server; books without genres are always kept';

    return (
      <FieldSet legend="Genres and Content Types">
        <FormGroup>
          <FormLabel>{'Allowed Genres'}</FormLabel>

          <FormInputGroup
            type={inputTypes.TEXT_TAG}
            name="allowedGenres"
            value={value.allowedGenres || []}
            helpTexts={[
              'When set, a book needs one of these genres. Books without genre data are kept',
              genreHint
            ]}
            kind={kinds.SUCCESS}
            delimiters={['Tab', 'Enter', ',']}
            onChange={this.onChange}
          />
        </FormGroup>

        <FormGroup>
          <FormLabel>{'Blocked Genres'}</FormLabel>

          <FormInputGroup
            type={inputTypes.TEXT_TAG}
            name="blockedGenres"
            value={value.blockedGenres || []}
            helpText='Skip books with any of these genres. Whole words match, so "romance" also blocks "Contemporary Romance"'
            kind={kinds.DANGER}
            delimiters={['Tab', 'Enter', ',']}
            onChange={this.onChange}
          />
        </FormGroup>

        {
          contentTypes.map((contentType) => {
            return (
              <FormGroup key={contentType.key}>
                <FormLabel>{contentType.label}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name={contentType.key}
                  value={!!value[contentType.key]}
                  helpText={contentType.helpText}
                  onChange={this.onChange}
                />
              </FormGroup>
            );
          })
        }
      </FieldSet>
    );
  }
}

TalearrContentFilterFields.propTypes = {
  field: PropTypes.object,
  onInputChange: PropTypes.func.isRequired
};

export default TalearrContentFilterFields;
