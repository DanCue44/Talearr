import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { ageRatingOptions, kindOptions, monitorOptions, readError } from './comicsFormat';
import styles from './Comics.css';

// Talearr: search AniList and Hardcover for a manga or comic series and add it.
class ComicsAdd extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      term: '',
      kind: 'manga',
      rootFolderId: null,
      monitor: 'all',
      ageRatingOverride: '',
      rootFolders: [],
      ageRatings: [],
      isSearching: false,
      addingKey: null,
      error: null,
      notices: [],
      results: null
    };
  }

  componentDidMount() {
    createAjaxRequest({ url: '/talearr/comics/rootfolders' }).request.done((rootFolders) => {
      this.setState({ rootFolders }, this.pickRootFolder);
    });

    createAjaxRequest({ url: '/talearr/comics/status' }).request.done((status) => {
      this.setState({ ageRatings: status.ageRatings, error: status.available ? null : status.reason });
    });
  }

  //
  // Helpers

  getFolderOptions() {
    const { rootFolders, kind } = this.state;

    return rootFolders
      .filter((folder) => folder.kind === 'any' || folder.kind === kind)
      .map((folder) => ({
        key: folder.id,
        value: folder.isKids ? `${folder.path} (kids, up to ${folder.maxAgeRating || 'any rating'})` : folder.path
      }));
  }

  pickRootFolder = () => {
    const options = this.getFolderOptions();

    if (!options.some((option) => option.key === this.state.rootFolderId)) {
      this.setState({ rootFolderId: options.length ? options[0].key : null });
    }
  };

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value }, name === 'kind' ? this.pickRootFolder : undefined);
  };

  onSearchPress = () => {
    const { term, kind } = this.state;

    if (!term.trim()) {
      return;
    }

    this.setState({ isSearching: true, error: null, results: null, notices: [] });

    const { request } = createAjaxRequest({
      url: '/talearr/comics/lookup',
      data: { term, kind }
    });

    request.done((response) => {
      this.setState({ isSearching: false, results: response.results, notices: response.notices });
    });

    request.fail((xhr) => {
      this.setState({ isSearching: false, error: readError(xhr, 'Search failed.') });
    });
  };

  onKeyDown = (event) => {
    if (event.key === 'Enter') {
      this.onSearchPress();
    }
  };

  onAddPress = (result) => {
    const { kind, rootFolderId, monitor, ageRatingOverride } = this.state;
    const key = `${result.aniListId}-${result.hardcoverSeriesId}`;

    this.setState({ addingKey: key, error: null });

    const { request } = createAjaxRequest({
      url: '/talearr/comics/series',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        aniListId: result.aniListId,
        hardcoverSeriesId: result.hardcoverSeriesId,
        kind: result.kind || kind,
        rootFolderId,
        monitor,
        ageRatingOverride: ageRatingOverride || null
      })
    });

    request.done((series) => {
      this.setState({ addingKey: null });
      this.props.history.push(`/comics/series/${series.id}`);
    });

    request.fail((xhr) => {
      this.setState({ addingKey: null, error: readError(xhr, 'Unable to add the series.') });
    });
  };

  //
  // Render

  render() {
    const { term, kind, rootFolderId, monitor, ageRatingOverride, ageRatings, isSearching, addingKey, error, notices, results } = this.state;
    const folderOptions = this.getFolderOptions();

    return (
      <PageContent title="Add Comics">
        <PageContentBody>
          <div className={styles.searchRow} onKeyDown={this.onKeyDown}>
            <div className={styles.searchInput}>
              <FormInputGroup
                type={inputTypes.TEXT}
                name="term"
                value={term}
                placeholder="Series name, for example Witch Hat Atelier or Saga"
                onChange={this.onInputChange}
              />
            </div>

            <SpinnerButton
              kind={kinds.PRIMARY}
              isSpinning={isSearching}
              onPress={this.onSearchPress}
            >
              {'Search'}
            </SpinnerButton>
          </div>

          <Form>
            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Kind'}</FormLabel>
              <FormInputGroup type={inputTypes.SELECT} name="kind"
                values={kindOptions} value={kind}
                onChange={this.onInputChange}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Folder'}</FormLabel>
              {
                folderOptions.length ?
                  <FormInputGroup type={inputTypes.SELECT} name="rootFolderId"
                    values={folderOptions} value={rootFolderId}
                    onChange={this.onInputChange}
                  /> :
                  <Alert kind={kinds.WARNING}>
                    {`No folder for ${kind === 'comic' ? 'comics' : 'manga'} yet. Add one in `}
                    <Link to="/comics/settings">{'Comics settings'}</Link>
                    {'.'}
                  </Alert>
              }
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Monitor'}</FormLabel>
              <FormInputGroup type={inputTypes.SELECT} name="monitor"
                values={monitorOptions} value={monitor}
                onChange={this.onInputChange}
              />
            </FormGroup>

            <FormGroup size={sizes.MEDIUM}>
              <FormLabel>{'Age rating'}</FormLabel>
              <FormInputGroup
                type={inputTypes.SELECT}
                name="ageRatingOverride"
                values={ageRatingOptions(ageRatings, 'From the series data')}
                value={ageRatingOverride}
                helpText="Written into every file, so a reader's per-person age limit can use it. Set it when the series data is missing or wrong."
                onChange={this.onInputChange}
              />
            </FormGroup>
          </Form>

          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}
          {
            notices.map((notice) => {
              return (
                <Alert
                  key={notice}
                  kind={kinds.WARNING}
                  className={styles.notice}
                >
                  {notice}
                </Alert>
              );
            })
          }
          {isSearching ? <LoadingIndicator /> : null}

          {
            results && !results.length ?
              <Alert kind={kinds.INFO}>{'Nothing found. Try the English title, or another spelling.'}</Alert> :
              null
          }

          {
            (results || []).map((result) => {
              const key = `${result.aniListId}-${result.hardcoverSeriesId}`;

              return (
                <div key={key} className={styles.result}>
                  {
                    result.coverUrl ?
                      <img className={styles.resultCover} src={result.coverUrl}
                        alt="" loading="lazy"
                      /> :
                      <div className={styles.resultCover} />
                  }

                  <div className={styles.resultBody}>
                    <div className={styles.cardTitle}>
                      {result.title}
                      {result.year ? ` (${result.year})` : ''}
                    </div>

                    {result.alternateTitles && result.alternateTitles.length ? <div className={styles.alternate}>{result.alternateTitles.join(' / ')}</div> : null}

                    <div className={styles.badges}>
                      <Label kind={kinds.INFO}>{result.kind === 'comic' ? 'Comic' : 'Manga'}</Label>
                      {result.volumes ? <Label>{`${result.volumes} volumes`}</Label> : null}
                      {result.status ? <Label>{result.status}</Label> : null}
                      <Label kind={result.ageRating === 'Unknown' ? kinds.WARNING : kinds.DEFAULT}>{result.ageRating}</Label>
                      {result.aniListId ? <Label kind={kinds.DEFAULT}>{'AniList'}</Label> : null}
                      {result.hardcoverSeriesId ? <Label kind={kinds.DEFAULT}>{'Hardcover'}</Label> : null}
                    </div>

                    {result.authors ? <div className={styles.small}>{result.authors}</div> : null}
                    {result.overview ? <div className={styles.resultOverview}>{result.overview}</div> : null}
                  </div>

                  <div>
                    {
                      result.seriesId ?
                        <Link to={`/comics/series/${result.seriesId}`}>{'In library'}</Link> :
                        <SpinnerButton
                          kind={kinds.SUCCESS}
                          isSpinning={addingKey === key}
                          isDisabled={!rootFolderId || (!!addingKey && addingKey !== key)}
                          onPress={() => this.onAddPress(result)}
                        >
                          {'Add'}
                        </SpinnerButton>
                    }
                  </div>
                </div>
              );
            })
          }
        </PageContentBody>
      </PageContent>
    );
  }
}

ComicsAdd.propTypes = {
  history: PropTypes.object.isRequired
};

export default ComicsAdd;
