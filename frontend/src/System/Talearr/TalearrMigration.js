import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './TalearrMigration.css';

const sourceOptions = [
  { key: 'readarr', value: 'Readarr' },
  { key: 'bookshelf', value: 'Bookshelf (Readarr fork, Hardcover metadata)' },
  { key: 'lazylibrarian', value: 'LazyLibrarian' }
];

const readOptions = [
  { key: 'api', value: 'API of a running instance' },
  { key: 'upload', value: 'Upload a readarr.db or backup zip' },
  { key: 'path', value: 'readarr.db or backup zip on this server' }
];

const mediaTypeOptions = [
  { key: 'auto', value: 'Guess per root folder ("audio" in the path means audiobooks)' },
  { key: 'ebook', value: 'Ebooks' },
  { key: 'audiobook', value: 'Audiobooks' }
];

const columns = [
  { name: 'area', label: 'What', isVisible: true },
  { name: 'name', label: 'Name', isVisible: true },
  { name: 'action', label: 'Action', isVisible: true },
  { name: 'detail', label: 'Detail', isVisible: true }
];

const areaLabels = {
  tag: 'Tag',
  rootFolder: 'Root folder',
  qualityProfile: 'Quality profile',
  metadataProfile: 'Metadata profile',
  downloadClient: 'Download client',
  indexer: 'Indexer',
  author: 'Author',
  book: 'Book'
};

const actionKinds = {
  create: kinds.SUCCESS,
  map: kinds.INFO,
  monitor: kinds.INFO,
  exists: kinds.DEFAULT,
  skip: kinds.DEFAULT,
  unmatched: kinds.WARNING,
  conflict: kinds.DANGER,
  failed: kinds.DANGER
};

const filters = ['all', 'create', 'unmatched', 'conflict'];

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}

function parseMappings(text) {
  return (text || '')
    .split('\n')
    .map((line) => line.split('=>').map((part) => part.trim()))
    .filter((parts) => parts.length === 2 && parts[0] && parts[1])
    .map(([from, to]) => ({ from, to }));
}

// Talearr: guided migration from Readarr, Bookshelf or LazyLibrarian (docs/TALEARR_MIGRATION.md).
// Preview reads the source and shows a dry-run report; Apply carries out that plan. Nothing in
// the source is changed.
class TalearrMigration extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._timer = null;

    this.state = {
      isFetching: true,
      status: null,
      error: null,
      isUploading: false,
      isConfirmingApply: false,
      filter: 'all',
      form: {
        source: 'readarr',
        read: 'api',
        url: '',
        apiKey: '',
        backupPath: '',
        mediaType: 'auto',
        pathMappings: '',
        importRootFolders: true,
        importDownloadClients: true,
        importIndexers: true,
        enableImportedProviders: false,
        searchOnAdd: false
      }
    };
  }

  componentDidMount() {
    this.fetchStatus();
  }

  componentWillUnmount() {
    clearTimeout(this._timer);
  }

  //
  // Data

  fetchStatus = () => {
    clearTimeout(this._timer);

    createAjaxRequest({ url: '/talearr/migration' }).request
      .done((status) => {
        this.setState({ isFetching: false, status });

        if (status.state === 'queued' || status.state === 'running') {
          this._timer = setTimeout(this.fetchStatus, 1500);
        }
      })
      .fail((xhr) => {
        this.setState({ isFetching: false, error: readError(xhr, 'Unable to load the migration status.') });
      });
  };

  post(url, data, method = 'POST') {
    this.setState({ error: null });

    createAjaxRequest({
      url,
      method,
      dataType: 'json',
      contentType: 'application/json',
      data: data ? JSON.stringify(data) : undefined
    }).request
      .done((status) => {
        this.setState({ status });
        this.fetchStatus();
      })
      .fail((xhr) => {
        this.setState({ error: readError(xhr, 'The request failed.') });
      });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ form: { ...this.state.form, [name]: value } });
  };

  onFilterPress = (filter) => {
    this.setState({ filter });
  };

  onFileChange = (event) => {
    const file = event.target.files && event.target.files[0];

    if (!file) {
      return;
    }

    const formData = new FormData();
    formData.append('file', file);

    this.setState({ isUploading: true, error: null });

    createAjaxRequest({
      url: '/talearr/migration/upload',
      method: 'POST',
      processData: false,
      contentType: false,
      data: formData
    }).request
      .done((data) => {
        this.setState({ isUploading: false, form: { ...this.state.form, backupPath: data.backupPath } });
      })
      .fail((xhr) => {
        this.setState({ isUploading: false, error: readError(xhr, 'The upload failed.') });
      });
  };

  onPreviewPress = () => {
    const { form } = this.state;
    const useApi = form.source === 'lazylibrarian' || form.read === 'api';

    this.post('/talearr/migration/preview', {
      source: form.source,
      url: useApi ? form.url : null,
      apiKey: useApi ? form.apiKey : null,
      backupPath: useApi ? null : form.backupPath,
      mediaType: form.mediaType,
      pathMappings: parseMappings(form.pathMappings),
      importRootFolders: form.importRootFolders,
      importDownloadClients: form.importDownloadClients,
      importIndexers: form.importIndexers,
      enableImportedProviders: form.enableImportedProviders,
      searchOnAdd: form.searchOnAdd
    });
  };

  onApplyPress = () => {
    this.setState({ isConfirmingApply: true });
  };

  onApplyConfirm = () => {
    this.setState({ isConfirmingApply: false });
    this.post('/talearr/migration/apply');
  };

  onApplyCancel = () => {
    this.setState({ isConfirmingApply: false });
  };

  onDiscardPress = () => {
    this.post('/talearr/migration', null, 'DELETE');
  };

  //
  // Render

  renderForm() {
    const { form, isUploading } = this.state;
    const isLazyLibrarian = form.source === 'lazylibrarian';
    const read = isLazyLibrarian ? 'api' : form.read;

    return (
      <Form>
        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Source'}</FormLabel>
          <FormInputGroup
            type={inputTypes.SELECT}
            name="source"
            values={sourceOptions}
            value={form.source}
            onChange={this.onInputChange}
          />
        </FormGroup>

        {
          isLazyLibrarian ?
            null :
            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Read from'}</FormLabel>
              <FormInputGroup
                type={inputTypes.SELECT}
                name="read"
                values={readOptions}
                value={form.read}
                helpText="A backup zip is in the source's System > Backup; readarr.db is in its config folder. The file is copied and only the copy is opened."
                onChange={this.onInputChange}
              />
            </FormGroup>
        }

        {
          read === 'api' ?
            <div>
              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>{'URL'}</FormLabel>
                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="url"
                  value={form.url}
                  helpText={isLazyLibrarian ? 'For example http://lazylibrarian:5299' : 'For example http://readarr:8787 (include the URL base if it has one)'}
                  onChange={this.onInputChange}
                />
              </FormGroup>

              <FormGroup size={sizes.MEDIUM}>
                <FormLabel>{'API key'}</FormLabel>
                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="apiKey"
                  value={form.apiKey}
                  helpText="Only read, never stored. Settings > General in Readarr, Config > Interface in LazyLibrarian."
                  onChange={this.onInputChange}
                />
              </FormGroup>
            </div> :
            null
        }

        {
          read === 'upload' ?
            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'File'}</FormLabel>
              <div className={styles.fileInput}>
                <input
                  type="file"
                  accept=".db,.zip"
                  onChange={this.onFileChange}
                />
                <span className={styles.sub}>
                  {isUploading ? 'Uploading...' : (form.backupPath || 'readarr.db or a readarr_backup zip')}
                </span>
              </div>
            </FormGroup> :
            null
        }

        {
          read === 'path' ?
            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Path'}</FormLabel>
              <FormInputGroup
                type={inputTypes.TEXT}
                name="backupPath"
                value={form.backupPath}
                helpText="For example /readarr-config/readarr.db with the Readarr config folder mounted read-only into Talearr"
                onChange={this.onInputChange}
              />
            </FormGroup> :
            null
        }

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Media type'}</FormLabel>
          <FormInputGroup
            type={inputTypes.SELECT}
            name="mediaType"
            values={mediaTypeOptions}
            value={form.mediaType}
            helpText={isLazyLibrarian ? 'LazyLibrarian tracks ebook and audiobook wants separately; both are brought over unless you pick one.' : 'Readarr instances usually hold one media type each.'}
            onChange={this.onInputChange}
          />
        </FormGroup>

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Path mappings'}</FormLabel>
          <FormInputGroup
            type={inputTypes.TEXT_AREA}
            name="pathMappings"
            value={form.pathMappings}
            helpText="One per line, source path => Talearr path, for example /books => /ebooks, when the library is mounted at a different path here."
            onChange={this.onInputChange}
          />
        </FormGroup>

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Root folders'}</FormLabel>
          <FormInputGroup
            type={inputTypes.CHECK}
            name="importRootFolders"
            value={form.importRootFolders}
            helpText="Add the source's root folders that exist on this server"
            onChange={this.onInputChange}
          />
        </FormGroup>

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Download clients'}</FormLabel>
          <FormInputGroup
            type={inputTypes.CHECK}
            name="importDownloadClients"
            value={form.importDownloadClients}
            onChange={this.onInputChange}
          />
        </FormGroup>

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Indexers'}</FormLabel>
          <FormInputGroup
            type={inputTypes.CHECK}
            name="importIndexers"
            value={form.importIndexers}
            helpText="Indexers synced by Prowlarr are skipped: add Talearr as an app in Prowlarr instead"
            onChange={this.onInputChange}
          />
        </FormGroup>

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Enable imported clients and indexers'}</FormLabel>
          <FormInputGroup
            type={inputTypes.CHECK}
            name="enableImportedProviders"
            value={form.enableImportedProviders}
            helpText="Off: they are added disabled, so the source and Talearr do not both grab while the source still runs"
            onChange={this.onInputChange}
          />
        </FormGroup>

        <FormGroup size={sizes.MEDIUM}>
          <FormLabel>{'Search when added'}</FormLabel>
          <FormInputGroup
            type={inputTypes.CHECK}
            name="searchOnAdd"
            value={form.searchOnAdd}
            helpText="Search for the monitored books that are missing once the authors are added"
            onChange={this.onInputChange}
          />
        </FormGroup>
      </Form>
    );
  }

  renderReport(report) {
    const { filter } = this.state;
    const summary = report.summary || {};
    const entries = (report.entries || []).filter((e) => filter === 'all' || e.action === filter);

    return (
      <div>
        <div className={styles.heading}>
          {report.dryRun ? 'Preview' : 'Last migration'}
          {report.appName ? ` from ${report.appName}${report.version ? ` ${report.version}` : ''}` : ''}
        </div>

        <div className={styles.summary}>
          {`${summary.authorsMatched || 0} of ${summary.authors || 0} authors and ${summary.booksMatched || 0} of ${summary.booksMonitored || 0} monitored books matched. `}
          {`${summary.creates || 0} to create, ${summary.unmatched || 0} unmatched, ${summary.conflicts || 0} conflicts.`}
        </div>

        <div className={styles.filters}>
          {
            filters.map((f) => (
              <Button
                key={f}
                size={sizes.SMALL}
                kind={filter === f ? kinds.PRIMARY : kinds.DEFAULT}
                onPress={() => this.onFilterPress(f)}
              >
                {f === 'all' ? 'All' : f.charAt(0).toUpperCase() + f.slice(1)}
              </Button>
            ))
          }
        </div>

        <Table columns={columns}>
          <TableBody>
            {
              entries.map((entry, index) => (
                <TableRow key={`${entry.area}-${entry.name}-${index}`}>
                  <TableRowCell>{areaLabels[entry.area] || entry.area}</TableRowCell>
                  <TableRowCell>{entry.name}</TableRowCell>
                  <TableRowCell>
                    <Label kind={actionKinds[entry.action] || kinds.DEFAULT}>
                      {entry.action}
                    </Label>
                  </TableRowCell>
                  <TableRowCell className={styles.detail}>{entry.detail}</TableRowCell>
                </TableRow>
              ))
            }
          </TableBody>
        </Table>
      </div>
    );
  }

  render() {
    const { isFetching, status, error, isConfirmingApply } = this.state;
    const state = status ? status.state : 'idle';
    const isBusy = state === 'queued' || state === 'running';
    const report = status && status.report;

    return (
      <PageContent title="Migrate">
        <PageContentBody>
          <div className={styles.intro}>
            {'Bring a Readarr, Bookshelf or LazyLibrarian library over: authors, monitored books, root folders, tags, download clients and indexers. Preview first; it shows what will be created, what did not match and what conflicts. Files already on disk are matched by a rescan. Nothing in the source is changed or deleted.'}
          </div>

          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}

          {isFetching ? <LoadingIndicator /> : this.renderForm()}

          <div className={styles.buttons}>
            <SpinnerButton
              kind={kinds.PRIMARY}
              isSpinning={isBusy}
              isDisabled={isBusy}
              onPress={this.onPreviewPress}
            >
              {'Preview'}
            </SpinnerButton>

            <Button
              kind={kinds.SUCCESS}
              isDisabled={isBusy || !(status && status.hasPlan)}
              onPress={this.onApplyPress}
            >
              {'Apply'}
            </Button>

            <Button
              isDisabled={isBusy || !(status && status.hasPlan)}
              onPress={this.onDiscardPress}
            >
              {'Discard preview'}
            </Button>
          </div>

          {
            status && status.message ?
              <Alert kind={state === 'failed' ? kinds.DANGER : kinds.INFO}>
                {status.message}
                {isBusy && status.total ? ` (${status.done} of ${status.total})` : ''}
              </Alert> :
              null
          }

          {report ? this.renderReport(report) : null}

          <ConfirmModal
            isOpen={isConfirmingApply}
            kind={kinds.SUCCESS}
            title="Apply the migration"
            message="Adds what the preview shows as create or monitor. Authors are queued and added in the background; files on disk are matched as each author is added. The source stays as it is."
            confirmLabel="Apply"
            onConfirm={this.onApplyConfirm}
            onCancel={this.onApplyCancel}
          />
        </PageContentBody>
      </PageContent>
    );
  }
}

export default TalearrMigration;
