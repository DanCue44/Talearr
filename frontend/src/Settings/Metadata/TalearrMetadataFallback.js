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

// TaleArr: Open Library metadata fallback. Stored as a talearr.metadatafallback.* row in the
// Config table; Chaptarr ignores it.
const modeOptions = [
  { key: 'off', value: 'Off' },
  { key: 'whenPrimaryDown', value: 'When the metadata server is down' },
  { key: 'whenPrimaryDownShowSource', value: 'When the metadata server is down, and show the source' }
];

function getModeHelpText(value) {
  switch (value) {
    case 'off':
      return 'Search and add depend on the metadata server only.';
    case 'whenPrimaryDownShowSource':
      return 'As above, and search results from Open Library are labelled.';
    default:
      return 'While the metadata server is down (or a request to it fails), search and add use Open Library, which needs no API key. Refresh never uses it: existing authors and books are kept as they are and are updated from the metadata server once it is back.';
  }
}

function getValue(setting) {
  return setting ? setting.value : null;
}

function formatStatus(settings) {
  const activeSource = getValue(settings.activeSource);
  const lastUsed = getValue(settings.lastUsed);
  const lastOperation = getValue(settings.lastOperation);
  const lastError = getValue(settings.lastError);

  const parts = [];

  if (activeSource) {
    parts.push(`Search and add currently use: ${activeSource}.`);
  }

  parts.push(lastUsed ?
    `Fallback last used ${new Date(lastUsed).toLocaleString()} (${lastOperation}).` :
    'Fallback not used since TaleArr started.');

  if (lastError) {
    parts.push(`Last fallback problem: ${lastError}.`);
  }

  return parts.join(' ');
}

function TalearrMetadataFallback(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  const mode = settings.mode || { value: 'whenPrimaryDown' };

  return (
    <FieldSet legend="Metadata fallback">
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {'Unable to load the TaleArr metadata fallback settings'}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            <FormGroup>
              <FormLabel>{'Metadata fallback'}</FormLabel>

              <FormInputGroup
                type={inputTypes.SELECT}
                name="mode"
                values={modeOptions}
                helpText={getModeHelpText(mode.value)}
                onChange={onInputChange}
                {...mode}
              />
            </FormGroup>

            <Alert kind={getValue(settings.primaryUnavailable) ? kinds.WARNING : kinds.INFO}>
              {formatStatus(settings)}
            </Alert>
          </Form>
      }
    </FieldSet>
  );
}

TalearrMetadataFallback.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default TalearrMetadataFallback;
