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

// Talearr: make EPUB copies of Kindle ebooks (AZW3, MOBI). Stored by Talearr only
// (Chaptarr ignores the settings).
function TalearrEpub(props) {
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
  const available = hasSettings && settings.converterAvailable ? settings.converterAvailable.value : true;
  const keepOriginal = hasSettings && settings.keepOriginal ? settings.keepOriginal.value : true;

  return (
    <FieldSet legend="Convert to EPUB">
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {'Unable to load the Talearr EPUB settings'}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            {
              !available &&
                <Alert kind={kinds.WARNING}>
                  {'KindleUnpack and Python 3 were not found, so ebooks cannot be converted. They are part of the Talearr Docker image.'}
                </Alert>
            }

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Convert after import'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="convertAfterImport"
                helpText="After an ebook in a Kindle format (AZW3 or MOBI) is imported, make an EPUB of it in the same folder, in the background. Readers and Audiobookshelf open EPUB everywhere. Books with DRM, and old MOBI files without the Kindle 8 part, cannot be converted and keep their file. Off by default."
                onChange={onInputChange}
                {...settings.convertAfterImport}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Keep the Kindle file'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="keepOriginal"
                helpText="Keep the AZW3 or MOBI next to the new EPUB. Turned off, the Kindle file is removed (to the recycle bin when one is set) once the EPUB has been checked and added."
                onChange={onInputChange}
                {...settings.keepOriginal}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Existing library'}</FormLabel>

              <div>
                <SpinnerButton
                  kind={kinds.WARNING}
                  isSpinning={!!isConvertingLibrary}
                  isDisabled={!!isConvertingLibrary || !available}
                  onPress={() => setIsConfirmOpen(true)}
                >
                  {'Convert library to EPUB'}
                </SpinnerButton>
              </div>
            </FormGroup>
          </Form>
      }

      <ConfirmModal
        isOpen={isConfirmOpen}
        kind={kinds.WARNING}
        title="Convert library to EPUB"
        message={
          keepOriginal ?
            'Make an EPUB of every Kindle ebook in the library that does not have one yet, one book at a time. The Kindle files are kept. A book that cannot be converted keeps its file and the run goes on.' :
            'Make an EPUB of every Kindle ebook in the library that does not have one yet, one book at a time. Each Kindle file is removed (to the recycle bin when one is set) after its EPUB is checked and added. A book that cannot be converted keeps its file and the run goes on.'
        }
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

TalearrEpub.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  isConvertingLibrary: PropTypes.bool,
  onInputChange: PropTypes.func.isRequired,
  onConvertLibraryPress: PropTypes.func.isRequired
};

export default TalearrEpub;
