import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Label from 'Components/Label';
import IconButton from 'Components/Link/IconButton';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, inputTypes, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { ageRatingOptions, readError } from './comicsFormat';
import styles from './Comics.css';

const folderKindOptions = [
  { key: 'manga', value: 'Manga' },
  { key: 'comic', value: 'Comics' },
  { key: 'any', value: 'Both' }
];

const folderKindLabels = { manga: 'Manga', comic: 'Comics', any: 'Both' };

const folderColumns = [
  { name: 'path', label: 'Folder', isVisible: true },
  { name: 'kind', label: 'For', isVisible: true },
  { name: 'kids', label: 'Kids library', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

// Talearr: comics folders, which releases to accept, indexer categories and naming.
class ComicsSettings extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      isSaving: false,
      isAdding: false,
      error: null,
      saved: false,
      folders: [],
      settings: null,
      categoriesText: '',
      ageRatings: [],
      newFolder: { path: '', kind: 'manga', isKids: false, maxAgeRating: '' }
    };
  }

  componentDidMount() {
    this.load();
  }

  //
  // Data

  load = () => {
    const status = createAjaxRequest({ url: '/talearr/comics/status' }).request;
    status.done((result) => {
      this.setState({ ageRatings: result.ageRatings, error: result.available ? null : result.reason });
    });

    createAjaxRequest({ url: '/talearr/comics/rootfolders' }).request.done((folders) => {
      this.setState({ folders });
    });

    const { request } = createAjaxRequest({ url: '/talearr/comics/settings' });
    request.done((settings) => {
      this.setState({ isFetching: false, settings, categoriesText: settings.categories.join(', ') });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load comics settings.') });
    });
  };

  //
  // Listeners

  onSettingChange = ({ name, value }) => {
    this.setState({ settings: { ...this.state.settings, [name]: value }, saved: false });
  };

  onCategoriesChange = ({ value }) => {
    this.setState({ categoriesText: value, saved: false });
  };

  onNewFolderChange = ({ name, value }) => {
    this.setState({ newFolder: { ...this.state.newFolder, [name]: value } });
  };

  onSavePress = () => {
    const categories = this.state.categoriesText.split(/[\s,]+/).map((c) => parseInt(c)).filter((c) => c > 0);
    this.setState({ isSaving: true, error: null });

    const { request } = createAjaxRequest({
      url: '/talearr/comics/settings',
      method: 'PUT',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...this.state.settings, categories })
    });

    request.done((settings) => {
      this.setState({ isSaving: false, saved: true, settings, categoriesText: settings.categories.join(', ') });
    });

    request.fail((xhr) => {
      this.setState({ isSaving: false, error: readError(xhr, 'Unable to save comics settings.') });
    });
  };

  onAddFolderPress = () => {
    const { newFolder } = this.state;
    this.setState({ isAdding: true, error: null });

    const { request } = createAjaxRequest({
      url: '/talearr/comics/rootfolders',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({ ...newFolder, maxAgeRating: newFolder.isKids ? newFolder.maxAgeRating || null : null })
    });

    request.done((folder) => {
      this.setState({
        isAdding: false,
        folders: [...this.state.folders, folder],
        newFolder: { path: '', kind: newFolder.kind, isKids: false, maxAgeRating: '' }
      });
    });

    request.fail((xhr) => {
      this.setState({ isAdding: false, error: readError(xhr, 'Unable to add the folder.') });
    });
  };

  onDeleteFolderPress = (folder) => {
    const { request } = createAjaxRequest({ url: `/talearr/comics/rootfolders/${folder.id}`, method: 'DELETE' });

    request.done(() => {
      this.setState({ folders: this.state.folders.filter((f) => f.id !== folder.id) });
    });

    request.fail((xhr) => {
      this.setState({ error: readError(xhr, 'Unable to remove the folder.') });
    });
  };

  //
  // Render

  render() {
    const { isFetching, isSaving, isAdding, error, saved, folders, settings, categoriesText, ageRatings, newFolder } = this.state;

    return (
      <PageContent title="Comics Settings">
        <PageContentBody>
          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}
          {isFetching ? <LoadingIndicator /> : null}

          <div className={styles.sectionTitle}>{'Folders'}</div>
          <div className={styles.intro}>
            {'One folder for manga and one for comics works best: readers like Kavita and Komga can then read each the right way round. A kids folder only accepts series rated up to its limit; point a child\'s reader account at it.'}
          </div>

          {
            folders.length ?
              <Table columns={folderColumns}>
                <TableBody>
                  {
                    folders.map((folder) => {
                      return (
                        <TableRow key={folder.id}>
                          <TableRowCell>{folder.path}</TableRowCell>
                          <TableRowCell>{folderKindLabels[folder.kind] || folder.kind}</TableRowCell>
                          <TableRowCell>
                            {folder.isKids ? <Label kind={kinds.INFO}>{`Up to ${folder.maxAgeRating || 'any rating'}`}</Label> : null}
                          </TableRowCell>
                          <TableRowCell>
                            <IconButton name={icons.DELETE} title="Remove folder"
                              onPress={() => this.onDeleteFolderPress(folder)}
                            />
                          </TableRowCell>
                        </TableRow>
                      );
                    })
                  }
                </TableBody>
              </Table> :
              null
          }

          <Form>
            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'New folder'}</FormLabel>
              <FormInputGroup type={inputTypes.PATH} name="path"
                value={newFolder.path} onChange={this.onNewFolderChange}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'For'}</FormLabel>
              <FormInputGroup type={inputTypes.SELECT} name="kind"
                values={folderKindOptions} value={newFolder.kind}
                onChange={this.onNewFolderChange}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Kids library'}</FormLabel>
              <FormInputGroup type={inputTypes.CHECK} name="isKids"
                value={newFolder.isKids} onChange={this.onNewFolderChange}
              />
            </FormGroup>

            {
              newFolder.isKids ?
                <FormGroup size={sizes.MEDIUM}>
                  <FormLabel>{'Highest age rating'}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="maxAgeRating"
                    values={ageRatingOptions(ageRatings, 'Choose a rating')}
                    value={newFolder.maxAgeRating}
                    helpText="Series without a rating are refused until you set one on the series."
                    onChange={this.onNewFolderChange}
                  />
                </FormGroup> :
                null
            }

            <SpinnerButton kind={kinds.PRIMARY} isSpinning={isAdding}
              isDisabled={!newFolder.path} onPress={this.onAddFolderPress}
            >
              {'Add folder'}
            </SpinnerButton>
          </Form>

          {
            settings ?
              <div className={styles.section}>
                <div className={styles.sectionTitle}>{'Releases'}</div>
                <Form>
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Digital'}</FormLabel>
                    <FormInputGroup type={inputTypes.CHECK} name="allowDigital"
                      value={settings.allowDigital} helpText="Official digital releases (always preferred)"
                      onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Scans'}</FormLabel>
                    <FormInputGroup type={inputTypes.CHECK} name="allowScan"
                      value={settings.allowScan} helpText="Scans of the printed book, and releases not marked digital"
                      onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'c2c scans'}</FormLabel>
                    <FormInputGroup type={inputTypes.CHECK} name="allowC2C"
                      value={settings.allowC2C} helpText="Cover to cover scans, ads and all (used last)"
                      onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Prefer CBZ'}</FormLabel>
                    <FormInputGroup type={inputTypes.CHECK} name="preferCbz"
                      value={settings.preferCbz} onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Volume packs'}</FormLabel>
                    <FormInputGroup type={inputTypes.CHECK} name="allowPacks"
                      value={settings.allowPacks} helpText="Accept a release holding several volumes (v01-v10)"
                      onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Smallest volume (MB)'}</FormLabel>
                    <FormInputGroup type={inputTypes.NUMBER} name="minimumVolumeSizeMb"
                      value={settings.minimumVolumeSizeMb} helpText="Smaller files are almost always single chapters"
                      onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Indexer categories'}</FormLabel>
                    <FormInputGroup type={inputTypes.TEXT} name="categories"
                      value={categoriesText} helpText="Newznab / Torznab categories searched for comics, comma separated. 7030 is Books > Comics."
                      onChange={this.onCategoriesChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Automatic search'}</FormLabel>
                    <FormInputGroup type={inputTypes.CHECK} name="automaticSearch"
                      value={settings.automaticSearch} helpText="Search for missing volumes twice a day, and when a series is added"
                      onChange={this.onSettingChange}
                    />
                  </FormGroup>
                </Form>

                <div className={styles.sectionTitle}>{'Naming'}</div>
                <Form>
                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Manga folder'}</FormLabel>
                    <FormInputGroup type={inputTypes.TEXT} name="mangaFolderFormat"
                      value={settings.mangaFolderFormat} onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'Comics folder'}</FormLabel>
                    <FormInputGroup type={inputTypes.TEXT} name="comicFolderFormat"
                      value={settings.comicFolderFormat} onChange={this.onSettingChange}
                    />
                  </FormGroup>

                  <FormGroup size={sizes.MEDIUM}>
                    <FormLabel>{'File name'}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="fileFormat"
                      value={settings.fileFormat}
                      helpText="Tokens: {Series Title} {Series Year} {Volume:00} {Year} {Volume Title}. Rename Files on a series applies a change."
                      onChange={this.onSettingChange}
                    />
                  </FormGroup>
                </Form>

                <SpinnerButton kind={kinds.PRIMARY} isSpinning={isSaving}
                  onPress={this.onSavePress}
                >
                  {'Save'}
                </SpinnerButton>
                {saved ? <span className={styles.small}>{' Saved.'}</span> : null}

                <div className={styles.section}>
                  <div className={styles.sectionTitle}>{'Download clients'}</div>
                  <div className={styles.intro}>
                    {'Comics download into each client\'s Comics Category (Settings > Download Clients, "comics" unless changed), apart from books. Leave it empty to use the ebook category.'}
                  </div>

                  <div className={styles.sectionTitle}>{'Readers'}</div>
                  <div className={styles.intro}>
                    {'Add Kavita or Komga under Settings > Connect. Talearr then asks them to scan a series as soon as a volume is filed. Give each child their own reader account with an age limit.'}
                  </div>
                </div>
              </div> :
              null
          }
        </PageContentBody>
      </PageContent>
    );
  }
}

export default ComicsSettings;
