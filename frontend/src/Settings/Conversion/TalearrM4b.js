import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds, sizes } from 'Helpers/Props';

// TaleArr: merge multi-file audiobooks into one chaptered M4B. Stored by TaleArr only
// (Chaptarr ignores the setting).
function TalearrM4b(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  return (
    <FieldSet legend="Merge to M4B">
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {'Unable to load the TaleArr M4B settings'}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Merge after import'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="convertAfterImport"
                helpText="After an audiobook made of several mp3 or m4a files is imported, merge it into one M4B with a chapter per file (or the files' own chapters), the cover and the book's tags. Runs in the background with FFmpeg. The original files are removed only after the M4B is checked (same length within 1%). Off by default."
                onChange={onInputChange}
                {...settings.convertAfterImport}
              />
            </FormGroup>

            <Alert kind={kinds.INFO}>
              {'Any single book can also be merged with Convert to M4B on its page. The bitrate, upscale and channel settings above are used when the files have to be re-encoded; m4a files that are already AAC are copied without re-encoding.'}
            </Alert>
          </Form>
      }
    </FieldSet>
  );
}

TalearrM4b.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default TalearrM4b;
