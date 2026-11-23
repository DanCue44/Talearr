import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds } from 'Helpers/Props';

// Talearr: Book Titles on Settings > Metadata. Stored as a talearr.titles.* row in the Config
// table; Chaptarr ignores it.
function TalearrTitles(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  const cleanSeriesFromTitles = settings.cleanSeriesFromTitles || { value: false };

  return (
    <FieldSet legend="Book Titles">
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {'Unable to load the Talearr book title settings'}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            <FormGroup>
              <FormLabel>{'Clean Series From Titles'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="cleanSeriesFromTitles"
                helpText="Show and name books without the series in the title, like Audible and Audiobookshelf: 'Viridian Gate Online: The Lich Priest: The Viridian Gate Archives, Book 5' becomes 'The Lich Priest'. Removes a series name and number at the end, and a 'Name: ' start that matches the series or that other books of the series share. Searches try the full metadata title first, then the clean one, and releases named either way match. Use Rename Files to rename books already on disk. A Display Title set in Edit Book always wins."
                onChange={onInputChange}
                {...cleanSeriesFromTitles}
              />
            </FormGroup>
          </Form>
      }
    </FieldSet>
  );
}

TalearrTitles.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default TalearrTitles;
