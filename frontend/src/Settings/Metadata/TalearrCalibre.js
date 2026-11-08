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

// Talearr: an optional Calibre library that Retry Match and Manual Import read (never write)
// to identify ebooks. Stored as a talearr.calibre.* row in the Config table; Chaptarr ignores it.
function getValue(setting) {
  return setting ? setting.value : null;
}

function TalearrCalibre(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  const libraryPath = settings.libraryPath || { value: '' };
  const savedPath = getValue(settings.libraryPath);
  const bookCount = getValue(settings.bookCount);
  const problem = getValue(settings.problem);

  return (
    <FieldSet legend="Calibre library">
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {'Unable to load the Talearr Calibre settings'}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            <FormGroup>
              <FormLabel>{'Calibre library folder'}</FormLabel>

              <FormInputGroup
                type={inputTypes.PATH}
                name="libraryPath"
                helpText="Optional. The folder that holds Calibre's metadata.db. When set, Retry Match and Manual Import use Calibre's author, title, ISBN and language for ebooks Calibre already knows: files inside this folder by their place in it, other files when their ISBN names exactly one Calibre book. Talearr only reads metadata.db and never changes it. The metadata provider still finds the edition. In Docker, mount the Calibre library into the Talearr container (read-only is fine) and enter the path inside the container. Leave empty to turn this off."
                onChange={onInputChange}
                {...libraryPath}
              />
            </FormGroup>

            {
              !!savedPath && !libraryPath.pending && (problem || bookCount != null) &&
                <Alert kind={problem ? kinds.WARNING : kinds.INFO}>
                  {problem || `Calibre library found: ${bookCount} books.`}
                </Alert>
            }
          </Form>
      }
    </FieldSet>
  );
}

TalearrCalibre.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default TalearrCalibre;
