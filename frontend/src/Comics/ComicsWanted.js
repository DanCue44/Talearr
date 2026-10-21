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
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { formatDate, readError, stateKinds, stateLabels, volumeLabel } from './comicsFormat';

const columns = [
  { name: 'series', label: 'Series', isVisible: true },
  { name: 'volume', label: 'Volume', isVisible: true },
  { name: 'released', label: 'Released', isVisible: true },
  { name: 'state', label: 'Status', isVisible: true }
];

// Talearr: monitored comic volumes that are out but not downloaded.
class ComicsWanted extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isFetching: true,
      isSearching: false,
      error: null,
      message: null,
      items: []
    };
  }

  componentDidMount() {
    this.fetchWanted();
  }

  //
  // Data

  fetchWanted = () => {
    this.setState({ isFetching: true });

    const { request } = createAjaxRequest({ url: '/talearr/comics/wanted' });

    request.done((items) => {
      this.setState({ isFetching: false, error: null, items });
    });

    request.fail((xhr) => {
      this.setState({ isFetching: false, error: readError(xhr, 'Unable to load wanted volumes.') });
    });
  };

  onRefreshPress = () => {
    this.fetchWanted();
  };

  onSearchPress = () => {
    this.setState({ isSearching: true });

    const { request } = createAjaxRequest({ url: '/talearr/comics/search/missing', method: 'POST' });

    request.always(() => {
      this.setState({ isSearching: false, message: 'Searching for every missing volume. This can take a few minutes.' });
    });
  };

  //
  // Render

  render() {
    const { isFetching, isSearching, error, message, items } = this.state;

    return (
      <PageContent title="Wanted Comics">
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton label="Search All" iconName={icons.SEARCH}
              isSpinning={isSearching} onPress={this.onSearchPress}
            />
            <PageToolbarButton label="Refresh" iconName={icons.REFRESH}
              isSpinning={isFetching} onPress={this.onRefreshPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          {message ? <Alert kind={kinds.INFO}>{message}</Alert> : null}
          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}
          {isFetching && !items.length ? <LoadingIndicator /> : null}
          {!isFetching && !error && !items.length ? <Alert kind={kinds.SUCCESS}>{'Every monitored volume that is out has been downloaded.'}</Alert> : null}

          {
            items.length ?
              <Table columns={columns}>
                <TableBody>
                  {
                    items.map(({ volume, seriesId, seriesTitle }) => {
                      return (
                        <TableRow key={volume.id}>
                          <TableRowCell>
                            <Link to={`/comics/series/${seriesId}`}>{seriesTitle}</Link>
                          </TableRowCell>
                          <TableRowCell>{volumeLabel(volume.number)}</TableRowCell>
                          <TableRowCell>{formatDate(volume.releaseDate)}</TableRowCell>
                          <TableRowCell>
                            <Label kind={stateKinds[volume.state]}>{stateLabels[volume.state] || volume.state}</Label>
                          </TableRowCell>
                        </TableRow>
                      );
                    })
                  }
                </TableBody>
              </Table> :
              null
          }
        </PageContentBody>
      </PageContent>
    );
  }
}

export default ComicsWanted;
