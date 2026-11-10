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

// Talearr: grab policy settings. Stored by Talearr only (Chaptarr ignores them).
const abridgedPolicyOptions = [
  { key: 'unabridgedOnly', value: 'Unabridged only' },
  { key: 'abridgedOnly', value: 'Abridged only' },
  { key: 'preferUnabridged', value: 'Prefer unabridged' },
  { key: 'any', value: 'Any (no preference)' }
];

function getAbridgedHelpText(value) {
  switch (value) {
    case 'preferUnabridged':
      return 'Abridged audiobook releases can be grabbed, but always rank below unabridged ones.';
    case 'abridgedOnly':
      return 'Only abridged audiobook releases are grabbed. Before searching, Talearr monitors an abridged edition of the book when one is known; releases marked unabridged are skipped, and unmarked ones too unless the monitored edition is abridged. Books with their own abridged setting (for example from Allseerr) keep it.';
    case 'any':
      return 'Abridged and unabridged audiobook releases are treated the same.';
    default:
      return 'Abridged audiobook releases are skipped unless the wanted edition is itself abridged (from its title or runtime).';
  }
}

function TalearrGrabPolicy(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  const abridgedPolicy = settings.abridgedPolicy || { value: 'unabridgedOnly' };

  return (
    <FieldSet legend="Grab policy">
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {'Unable to load the Talearr grab policy'}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            <FormGroup>
              <FormLabel>{'Abridged audiobooks'}</FormLabel>

              <FormInputGroup
                type={inputTypes.SELECT}
                name="abridgedPolicy"
                values={abridgedPolicyOptions}
                helpText={getAbridgedHelpText(abridgedPolicy.value)}
                onChange={onInputChange}
                {...abridgedPolicy}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{'Allow bundles'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="allowBundles"
                helpText="Treat series bundles, omnibuses, box sets and collections like any other release. Off by default: bundles are used only when no single copy is available, and only the requested book (plus books on your wanted list) is kept."
                onChange={onInputChange}
                {...settings.allowBundles}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{'Fail rejected imports'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="failRejectedImports"
                helpText="When a download turns out to be the wrong book or has no audio or ebook files, mark it failed so it is blocklisted and the next-best release is searched for. Explicit interactive grabs are left in the queue."
                onChange={onInputChange}
                {...settings.failRejectedImports}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{'Fail stalled downloads'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="failStalledDownloads"
                helpText="When a download sits paused in NZBGet for 6 hours with nothing changing (for example a release with only par2 repair files left), mark it failed so it is blocklisted and the next-best release is searched for. Downloads you grabbed yourself only get a warning in the queue."
                onChange={onInputChange}
                {...settings.failStalledDownloads}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{'Automatic retry limit'}</FormLabel>

              <FormInputGroup
                type={inputTypes.NUMBER}
                name="maxAutomaticRetries"
                min={1}
                max={10}
                helpText="Failed downloads per book within 7 days before Talearr stops searching for a replacement automatically. Each step is written to History. Uses Automatic Redownload under Download Clients."
                onChange={onInputChange}
                {...settings.maxAutomaticRetries}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>{'Retry failed grabs'}</FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="retryFailedGrabs"
                helpText="When a release cannot be sent to the download client because the indexer is rate limited (429), returns a server error or times out, try the next release and then search the book again after 2, 10 and 30 minutes (up to the retry limit, waiting out Retry-After and indexer back-off). Steps are logged with a Talearr: prefix."
                onChange={onInputChange}
                {...settings.retryFailedGrabs}
              />
            </FormGroup>
          </Form>
      }
    </FieldSet>
  );
}

TalearrGrabPolicy.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default TalearrGrabPolicy;
