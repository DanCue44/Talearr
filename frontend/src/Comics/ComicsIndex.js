import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import { icons, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { isUnrated, ratingLabel, readError } from './comicsFormat';
import styles from './Comics.css';

// Talearr: the comics and manga library (notes: TALEARR_COMICS_PLAN.md).
class ComicsIndex extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._abortRequest = null;

    this.state = {
      isFetching: true,
      isSearching: false,
      error: null,
      message: null,
      items: []
    };
  }

  componentDidMount() {
    this.fetchSeries();
  }

  componentWillUnmount() {
    if (this._abortRequest) {
      this._abortRequest();
    }
  }

  //
  // Data

  fetchSeries = () => {
    if (this._abortRequest) {
      this._abortRequest();
    }

    this.setState({ isFetching: true });

    const { request, abortRequest } = createAjaxRequest({ url: '/talearr/comics/series' });
    this._abortRequest = abortRequest;

    request.done((items) => {
      this.setState({ isFetching: false, error: null, items });
    });

    request.fail((xhr) => {
      if (!xhr.aborted) {
        this.setState({ isFetching: false, error: readError(xhr, 'Unable to load comics.') });
      }
    });
  };

  //
  // Listeners

  onRefreshPress = () => {
    this.fetchSeries();
  };

  onSearchMissingPress = () => {
    this.setState({ isSearching: true });

    const { request } = createAjaxRequest({ url: '/talearr/comics/search/missing', method: 'POST' });

    request.always(() => {
      this.setState({ isSearching: false, message: 'Searching for every missing volume. Downloads show up on each series page.' });
    });
  };

  //
  // Render

  render() {
    const { isFetching, isSearching, error, message, items } = this.state;
    const unrated = items.filter((item) => isUnrated(item.effectiveAgeRating));

    return (
      <PageContent title="Comics">
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label="Add New"
              iconName={icons.ADD}
              to="/add/search?type=comics"
            />

            <PageToolbarButton
              label="Search Missing"
              iconName={icons.SEARCH}
              isSpinning={isSearching}
              onPress={this.onSearchMissingPress}
            />

            <PageToolbarButton
              label="Refresh"
              iconName={icons.REFRESH}
              isSpinning={isFetching}
              onPress={this.onRefreshPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <div className={styles.intro}>
            {'Manga and comics, by volume (beta). Talearr finds official digital releases first, converts them to CBZ with tags readers understand, and files them where Kavita or Komga can read them.'}
          </div>

          {message ? <Alert kind={kinds.INFO}>{message}</Alert> : null}
          {isFetching && !items.length ? <LoadingIndicator /> : null}
          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}

          {
            unrated.length ?
              <Alert kind={kinds.WARNING} className={styles.unrated}>
                {`${unrated.length} ${unrated.length === 1 ? 'series has' : 'series have'} no age rating, so a reader's age limit cannot use ${unrated.length === 1 ? 'it' : 'them'} and kids libraries refuse ${unrated.length === 1 ? 'it' : 'them'}: `}
                {
                  unrated.map(({ series }, index) => {
                    return (
                      <span key={series.id}>
                        {index ? ', ' : ''}
                        <Link to={`/comics/series/${series.id}`}>{series.title}</Link>
                      </span>
                    );
                  })
                }
                {'. Open a series to set its rating.'}
              </Alert> :
              null
          }

          {
            !isFetching && !error && !items.length ?
              <Alert kind={kinds.INFO}>
                {'No comics yet. Set up a comics folder in '}
                <Link to="/settings/comics">{'Comics settings'}</Link>
                {', then '}
                <Link to="/add/search?type=comics">{'add a series'}</Link>
                {'.'}
              </Alert> :
              null
          }

          <div className={styles.grid}>
            {
              items.map(({ series, effectiveAgeRating, volumeCount, downloadedCount, missingCount, downloadingCount }) => {
                return (
                  <Link
                    key={series.id}
                    className={styles.card}
                    to={`/comics/series/${series.id}`}
                  >
                    {
                      series.coverUrl ?
                        <img className={styles.cover} src={series.coverUrl}
                          alt="" loading="lazy"
                        /> :
                        <div className={styles.cover} />
                    }

                    <div className={styles.cardBody}>
                      <div className={styles.cardTitle} title={series.title}>{series.title}</div>
                      <div className={styles.cardMeta}>
                        {`${downloadedCount} of ${volumeCount} volumes`}
                        {downloadingCount ? `, ${downloadingCount} downloading` : ''}
                      </div>

                      <div className={styles.badges}>
                        <Label kind={kinds.INFO}>{series.kind === 'comic' ? 'Comic' : 'Manga'}</Label>
                        <Label kind={isUnrated(effectiveAgeRating) ? kinds.WARNING : kinds.DEFAULT}>{ratingLabel(effectiveAgeRating)}</Label>
                        {missingCount ? <Label kind={kinds.DANGER}>{`${missingCount} missing`}</Label> : null}
                        {series.monitored ? null : <Label kind={kinds.DISABLED}>{'Not monitored'}</Label>}
                      </div>
                    </div>
                  </Link>
                );
              })
            }
          </div>
        </PageContentBody>
      </PageContent>
    );
  }
}

export default ComicsIndex;
