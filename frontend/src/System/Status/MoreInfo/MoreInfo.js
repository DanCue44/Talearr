import React, { Component } from 'react';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItemDescription from 'Components/DescriptionList/DescriptionListItemDescription';
import DescriptionListItemTitle from 'Components/DescriptionList/DescriptionListItemTitle';
import FieldSet from 'Components/FieldSet';
import Link from 'Components/Link/Link';
import translate from 'Utilities/String/translate';

class MoreInfo extends Component {

  //
  // Render

  render() {
    return (
      <FieldSet legend={translate('MoreInfo')}>
        <DescriptionList>
          <DescriptionListItemTitle>{translate('Source')}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://github.com/DanCue44/Talearr">{'github.com/DanCue44/Talearr'}</Link>
          </DescriptionListItemDescription>

          <DescriptionListItemTitle>{'Upstream'}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://github.com/Chaptarr/chaptarr">{'Chaptarr (github.com/Chaptarr/chaptarr)'}</Link>
          </DescriptionListItemDescription>

          <DescriptionListItemTitle>{translate('Wiki')}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://github.com/DanCue44/Talearr/blob/main/docs/TALEARR_REFERENCE.md">{'Talearr reference'}</Link>
          </DescriptionListItemDescription>

          <DescriptionListItemTitle>{'Support'}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://github.com/DanCue44/Talearr/issues">{'github.com/DanCue44/Talearr/issues'}</Link>
          </DescriptionListItemDescription>

          <DescriptionListItemTitle>{'License'}</DescriptionListItemTitle>
          <DescriptionListItemDescription>
            <Link to="https://www.gnu.org/licenses/gpl-3.0.html">{'GNU GPL v3'}</Link>
          </DescriptionListItemDescription>
        </DescriptionList>
      </FieldSet>
    );
  }
}

MoreInfo.propTypes = {

};

export default MoreInfo;
