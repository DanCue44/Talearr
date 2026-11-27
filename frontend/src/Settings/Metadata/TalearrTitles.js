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

// Talearr: Book Titles and Audiobook Only on Settings > Metadata. Stored as talearr.titles.* and
// talearr.library.audiobookonly rows in the Config table; Chaptarr ignores them.
function TalearrTitles(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  const cleanSeriesFromTitles = settings.cleanSeriesFromTitles || { value: false };
  const audiobookOnly = settings.audiobookOnly || { value: false };

  const status = (
    <>
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
    </>
  );

  const isReady = hasSettings && !isFetching && !error;

  return (
    <>
      <FieldSet legend="Book Titles">
        {status}

        {
          isReady &&
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

      <FieldSet legend="Library Formats">
        {
          isReady &&
            <Form>
              <FormGroup>
                <FormLabel>{'Audiobook Only'}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="audiobookOnly"
                  helpText="For audiobook-only libraries. Books with no known audiobook edition (no audio edition in the metadata, no Audible ASIN) and ebook entries are hidden from the author, series and book lists, the calendar and Wanted, are not counted as missing and are not searched or grabbed automatically. Nothing is deleted or unmonitored: when a metadata refresh finds an audiobook edition the book shows again, and turning this off shows everything again. Entries that already have files always show."
                  onChange={onInputChange}
                  {...audiobookOnly}
                />
              </FormGroup>
            </Form>
        }
      </FieldSet>
    </>
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
