import PropTypes from 'prop-types';
import React, { useState } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { inputTypes, kinds, sizes } from 'Helpers/Props';

// TaleArr: merge multi-file audiobooks into one chaptered M4B. Stored by TaleArr only
// (Chaptarr ignores the setting).
function TalearrM4b(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    isConvertingLibrary,
    onInputChange,
    onConvertLibraryPress
  } = props;

  const [isConfirmOpen, setIsConfirmOpen] = useState(false);

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

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Existing library'}</FormLabel>

              <div>
                <SpinnerButton
                  kind={kinds.WARNING}
                  isSpinning={!!isConvertingLibrary}
                  isDisabled={!!isConvertingLibrary}
                  onPress={() => setIsConfirmOpen(true)}
                >
                  {'Convert library to M4B'}
                </SpinnerButton>
              </div>
            </FormGroup>
          </Form>
      }

      <ConfirmModal
        isOpen={isConfirmOpen}
        kind={kinds.WARNING}
        title="Convert library to M4B"
        message="Merge every audiobook in the library that is made of several mp3, m4a or m4b files into one chaptered M4B, one book at a time. Needs FFmpeg. Each M4B is checked (same length within 1%) before the original files are removed (to the recycle bin when one is set); a book that fails keeps its files. This can take hours on a large library."
        confirmLabel="Convert"
        onConfirm={() => {
          setIsConfirmOpen(false);
          onConvertLibraryPress();
        }}
        onCancel={() => setIsConfirmOpen(false)}
      />
    </FieldSet>
  );
}

TalearrM4b.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  isConvertingLibrary: PropTypes.bool,
  onInputChange: PropTypes.func.isRequired,
  onConvertLibraryPress: PropTypes.func.isRequired
};

export default TalearrM4b;
