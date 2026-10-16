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

const providerOptions = [
  { key: 'audible', value: 'Audible catalog' },
  { key: 'audnexus', value: 'Audnexus' },
  { key: 'off', value: 'Off' }
];

const enrichOptions = [
  { key: 'monitored', value: 'Monitored recordings' },
  { key: 'all', value: 'All audiobook editions of monitored books' },
  { key: 'off', value: 'Off (only when you press Refresh)' }
];

// TaleArr: recordings settings (docs/TALEARR_RECORDINGS.md). Stored as talearr.recordings.* rows
// in the Config table; Chaptarr ignores them.
function TalearrRecordings(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  const marketplaces = (settings.availableMarketplaces && settings.availableMarketplaces.value) || [];
  const marketplaceOptions = marketplaces.map((m) => {
    return { key: m.code, value: `${m.name} (${m.domain})` };
  });

  const provider = settings.provider ? settings.provider.value : 'audible';

  return (
    <FieldSet legend="Recordings">
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {'Unable to load the TaleArr recordings settings'}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Audible marketplace'}</FormLabel>

              <FormInputGroup
                type={inputTypes.SELECT}
                name="marketplace"
                values={marketplaceOptions}
                helpText="The Audible store used to look up narrator, runtime and abridged or unabridged, and for the links on the book page"
                onChange={onInputChange}
                {...settings.marketplace}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Audible facts from'}</FormLabel>

              <FormInputGroup
                type={inputTypes.SELECT}
                name="provider"
                values={providerOptions}
                helpText="Audible catalog: batch lookups and search, low volume, cached for 30 days. Audnexus: one book per request, no search. Off: only the metadata server's facts are used."
                onChange={onInputChange}
                {...settings.provider}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Audnexus fallback'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="audnexusFallback"
                helpText="Ask Audnexus (api.audnex.us, a community run server) when the Audible catalog has no answer or is throttling. Off by default."
                isDisabled={provider !== 'audible'}
                onChange={onInputChange}
                {...settings.audnexusFallback}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Background lookups'}</FormLabel>

              <FormInputGroup
                type={inputTypes.SELECT}
                name="enrich"
                values={enrichOptions}
                helpText="Which editions the twice daily refresh looks up. The Refresh button on a book page always works."
                onChange={onInputChange}
                {...settings.enrich}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Search when added'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="searchOnAdd"
                helpText="Search for a recording right away when it is added from the book page, AllSeerr or the API"
                onChange={onInputChange}
                {...settings.searchOnAdd}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Keep both recordings'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="keepBoth"
                helpText="An unabridged download for a book you have as abridged is kept as its own recording instead of replacing the abridged files. Removing a recording is always your choice, on the book page."
                onChange={onInputChange}
                {...settings.keepBoth}
              />
            </FormGroup>

            <Alert kind={kinds.INFO}>
              {'The Audible catalog API is unofficial. TaleArr keeps requests low (batched, at most one a second, long cache) and never shares the data, but using it is at your own risk, as with Chaptarr\'s own Audible search.'}
            </Alert>
          </Form>
      }
    </FieldSet>
  );
}

TalearrRecordings.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default TalearrRecordings;
